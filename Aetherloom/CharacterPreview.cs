using System.Numerics;
using System.Drawing;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed unsafe class CharacterPreview : IDisposable
{
    // The eighth portrait target is shared with the game's party portraits.
    private const uint Slot = 7;
    private const int ActorIndex = 440 + (int)Slot;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition conditions;
    private readonly IObjectTable objects;
    private readonly IAddonLifecycle addons;
    private readonly IPluginLog log;
    private readonly IDataManager data;
    private JObject? selectedDesign, loadedDesign;
    private bool previewDesign;
    private string notes = "";
    private CharaView* view;
    private OffscreenRenderingManager* backgroundOwner;
    private Texture* previousBackground;
    private Texture* installedBackground;
    private enum Background { GameDefault, Black, White }
    private static readonly string[] BackgroundNames = ["Game default (grey)", "Black", "White"];
    private int background;
    private nint dragWindow;
    private Point dragOrigin;
    private ImGuiMouseButton dragButton;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    private bool requested;
    private nint player, textureId;
    private float aspect;
    private Vector2 rotate, pan;
    private float zoomDelta;
    private long lastImageOrStart, lastDraw;
    private bool cameraReady;
    private string status = "";
    private (uint State, bool BaseLoaded, string Stage)? lastProgress;

    public CharacterPreview(IFramework framework, IGameGui gameGui, ICondition conditions, IObjectTable objects, IAddonLifecycle addons, IPluginLog log, IDataManager data)
    {
        (this.framework, this.gameGui, this.conditions, this.objects, this.addons) = (framework, gameGui, conditions, objects, addons);
        this.log = log;
        this.data = data;
        framework.Update += Update;
        addons.RegisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
    }

    public void Close()
    {
        EndDrag();
        requested = false;
        textureId = 0;
        status = "";
    }

    public void Draw(JObject? design = null)
    {
        lastDraw = Environment.TickCount64;
        selectedDesign = design;
        if (design == null && previewDesign && requested) Close();
        if (design != null && (!requested || !previewDesign))
        {
            if (ImGui.Button("Preview selected design"))
            {
                previewDesign = true;
                requested = true;
                status = "";
            }
            ImGui.SameLine();
        }
        if ((!requested || previewDesign) && ImGui.Button("Preview current gear"))
        {
            previewDesign = false;
            requested = true;
            status = "";
        }
        if (!requested)
        {
            if (status.Length != 0) ImGui.TextWrapped(status);
            return;
        }

        if (ImGui.Button("Stop preview")) { Close(); return; }
        if (textureId == 0 || !ReferenceEquals(loadedDesign, previewDesign ? design : null))
        {
            ImGui.TextUnformatted(status.Length == 0 ? "Loading character preview..." : status);
            return;
        }

        var width = Math.Min(ImGui.GetContentRegionAvail().X, 380 * ImGui.GetIO().FontGlobalScale * aspect);
        if (width <= 0) return;
        ImGui.InvisibleButton("##character-camera", new Vector2(width, width / aspect),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        drawList.AddImage(new ImTextureID((ulong)textureId), min, max);
        var io = ImGui.GetIO();
        if (ImGui.IsItemHovered())
        {
            ImGuiP.SetItemUsingMouseWheel();
            zoomDelta -= io.MouseWheel;
        }
        HandleCameraDrag(io.FontGlobalScale);
        ImGui.TextDisabled("Left-drag to rotate, right-drag to pan, scroll to zoom.");
        ImGui.Combo("Preview backdrop", ref background, BackgroundNames, BackgroundNames.Length);
        if (notes.Length > 0) ImGui.TextWrapped(notes);
    }

    private void HandleCameraDrag(float scale)
    {
        if (ImGui.IsItemActivated() && GetCursorPos(out dragOrigin))
        {
            dragWindow = GetForegroundWindow();
            dragButton = ImGui.IsMouseClicked(ImGuiMouseButton.Right) ? ImGuiMouseButton.Right : ImGuiMouseButton.Left;
        }
        if (dragWindow == 0) return;
        if (GetForegroundWindow() != dragWindow || !ImGui.IsItemActive() || !ImGui.IsMouseDown(dragButton))
        {
            EndDrag();
            return;
        }
        ImGui.SetMouseCursor(ImGuiMouseCursor.None);
        if (!GetCursorPos(out var cursor) || cursor == dragOrigin) return;
        var delta = new Vector2(cursor.X - dragOrigin.X, cursor.Y - dragOrigin.Y) / scale;
        if (dragButton == ImGuiMouseButton.Left) rotate += delta;
        else pan += new Vector2(delta.X, -delta.Y);
        SetCursorPos(dragOrigin.X, dragOrigin.Y);
    }

    private void EndDrag()
    {
        if (dragWindow != 0 && GetForegroundWindow() == dragWindow)
            SetCursorPos(dragOrigin.X, dragOrigin.Y);
        dragWindow = 0;
    }

    private void Update(IFramework _)
    {
        if (!requested) { Release(); return; }
        var desired = previewDesign ? selectedDesign : null;
        if (view != null && !ReferenceEquals(desired, loadedDesign)) Release();
        if (conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || objects.LocalPlayer is not { } actor || (view != null && actor.Address != player))
        {
            Pause("Preview paused while the character loads.");
            return;
        }
        if (!gameGui.GetAddonByName("BannerParty").IsNull)
        {
            Pause("Preview paused for party portraits.");
            return;
        }
        if (Environment.TickCount64 - lastDraw > 3000)
        {
            Release();
            return;
        }
        if (view == null)
        {
            if (objects[ActorIndex] != null) { Pause("Waiting for the preview slot to become available."); return; }
            var source = (Character*)actor.Address;
            if (source->GetCharacterBase() == null || !source->IsReadyToDraw())
            {
                Pause("Waiting for your equipped character model.");
                return;
            }
            CharaViewModelData model = default;
            model.CopyFromCharacter(source);
            notes = "";
            if (desired != null)
            {
                if (desired["Equipment"] is not JObject gear || desired["Customize"] is not JObject look
                    || look.Value<uint>("ModelId") != 0)
                {
                    Stop("Select a player-character design to preview.");
                    return;
                }
                var currentHair = model.CustomizeData.Hairstyle;
                notes = PreviewOutfit.Apply(ref model, gear, look, data);
                var hair = look["Hairstyle"];
                log.Information($"Prepared design preview: current hair={currentHair}, design hair={hair?.Value<byte?>("Value")}, apply={hair?.Value<bool>("Apply") == true}, preview hair={model.CustomizeData.Hairstyle}. {notes}");
            }
            var space = IMemorySpace.GetUISpace();
            var memory = space == null ? null : (CharaView*)space->Malloc((ulong)sizeof(CharaView), 16);
            if (memory == null) { Stop("Could not allocate the character preview."); return; }
            view = memory->Ctor();
            view->ModelData = model;
            view->Initialize(null, Slot, 0);
            loadedDesign = desired;
            player = actor.Address;
            lastImageOrStart = Environment.TickCount64;
            lastProgress = null;
        }

        var native = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (native != null) view->Render(native->FrameCounter);
        var stage = "Waiting for the native character renderer.";
        if (view->State == 6)
        {
            UpdateBackground();
            var character = view->GetCharacter();
            var hasModel = character != null && character->GetCharacterBase() != null;
            if (!cameraReady && view->Camera != null && hasModel)
            {
                view->ResetPositions();
                cameraReady = true;
            }
            if (cameraReady && rotate != Vector2.Zero) view->SetCameraYawAndPitch(rotate.X, rotate.Y);
            if (cameraReady && pan != Vector2.Zero) view->SetCameraXAndY(pan.X, pan.Y);
            rotate = pan = Vector2.Zero;
            if (cameraReady && zoomDelta != 0) view->SetCameraDistance(zoomDelta);
            zoomDelta = 0;
            var targets = RenderTargetManager.Instance();
            var texture = targets == null ? null : targets->GetCharaViewTexture(Slot);
            if (cameraReady && hasModel && character->IsReadyToDraw()
                && texture != null && texture->D3D11ShaderResourceView != null && texture->ActualWidth > 0 && texture->ActualHeight > 0)
            {
                aspect = (float)texture->ActualWidth / texture->ActualHeight;
                textureId = (nint)texture->D3D11ShaderResourceView;
                ReportProgress("Displaying character texture.");
                lastImageOrStart = Environment.TickCount64;
                return;
            }
            stage = character == null ? "Waiting for the preview actor."
                : !hasModel ? "Waiting for the character model."
                : view->Camera == null ? "Waiting for the preview camera."
                : !character->IsReadyToDraw() ? $"Waiting for the preview actor to draw (render flags {character->RenderFlags})."
                : targets == null ? "Waiting for the render target manager."
                : texture == null ? "Waiting for the character texture."
                : texture->D3D11ShaderResourceView == null ? "Waiting for the character texture's shader view."
                : "Waiting for nonzero character texture dimensions.";
        }
        textureId = 0;
        ReportProgress(stage);
        if (Environment.TickCount64 - lastImageOrStart > 15000)
            Stop($"Preview timed out at state {view->State}. {stage}");
    }

    private void ReportProgress(string stage)
    {
        var progress = (view->State, view->CharacterLoaded, stage);
        if (lastProgress == progress) return;
        lastProgress = progress;
        status = stage;
        log.Information($"Preview slot {Slot}, state {view->State}, base loaded={view->CharacterLoaded}. {stage}");
    }

    private void UpdateBackground()
    {
        if (background == (int)Background.GameDefault)
        {
            if (backgroundOwner != null) RestoreBackground();
            return;
        }
        var renderer = OffscreenRenderingManager.Instance();
        var utility = CharacterUtility.Instance();
        if (renderer == null || utility == null) return;
        var handle = background == (int)Background.Black ? utility->BlackTextureResourceHandle : utility->WhiteTextureResourceHandle;
        if (handle == null || handle->Texture == null) return;
        var texture = handle->Texture;
        if (backgroundOwner == renderer && installedBackground == texture) return;
        RestoreBackground();

        // Transfer the slot's existing reference to this preview until it closes.
        texture->IncRef();
        previousBackground = renderer->BackgroundTextures[(int)Slot];
        renderer->BackgroundTextures[(int)Slot] = texture;
        installedBackground = texture;
        backgroundOwner = renderer;
        log.Information($"Installed {BackgroundNames[background]} renderer background for preview slot {Slot}.");
    }

    private void RestoreBackground()
    {
        if (backgroundOwner == null) return;
        if (OffscreenRenderingManager.Instance() == backgroundOwner
            && backgroundOwner->BackgroundTextures[(int)Slot].Value == installedBackground)
        {
            backgroundOwner->BackgroundTextures[(int)Slot] = previousBackground;
            installedBackground->DecRef();
        }
        else if (previousBackground != null)
        {
            // The renderer already replaced its copy and released that reference.
            previousBackground->DecRef();
        }
        backgroundOwner = null;
        installedBackground = previousBackground = null;
    }

    private void OnPartyPortraits(AddonEvent evt, AddonArgs args)
    {
        if (requested || view != null) Pause("Preview paused for party portraits.");
    }

    private void Pause(string reason)
    {
        if (status != reason) log.Information(reason);
        status = reason;
        Release();
    }

    private void Stop(string reason)
    {
        log.Warning(reason);
        Close();
        status = reason;
        Release();
    }

    private void Release()
    {
        EndDrag();
        if (view == null) return;
        textureId = 0;
        zoomDelta = 0;
        rotate = pan = Vector2.Zero;
        cameraReady = false;
        RestoreBackground();
        view->Release();
        view->Dtor(1);
        view = null;
    }

    public void Dispose()
    {
        framework.Update -= Update;
        addons.UnregisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
        Close();
        if (view != null) framework.RunOnFrameworkThread(Release).GetAwaiter().GetResult();
    }
}
