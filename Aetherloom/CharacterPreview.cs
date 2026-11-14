using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

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
    private CharaView* view;
    private bool requested;
    private nint player, textureId;
    private float aspect, rotate;
    private float zoomDelta;
    private long lastImageOrStart, lastDraw;
    private bool cameraReady;
    private string status = "";
    private (uint State, bool BaseLoaded, string Stage)? lastProgress;

    public CharacterPreview(IFramework framework, IGameGui gameGui, ICondition conditions, IObjectTable objects, IAddonLifecycle addons, IPluginLog log)
    {
        (this.framework, this.gameGui, this.conditions, this.objects, this.addons) = (framework, gameGui, conditions, objects, addons);
        this.log = log;
        framework.Update += Update;
        addons.RegisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
    }

    public void Close()
    {
        requested = false;
        textureId = 0;
        status = "";
    }

    public void Draw()
    {
        lastDraw = Environment.TickCount64;
        if (!requested)
        {
            if (ImGui.Button("Preview current gear"))
            {
                status = "";
                requested = true;
            }
            if (status.Length != 0) ImGui.TextWrapped(status);
            return;
        }

        if (ImGui.Button("Stop preview")) { Close(); return; }
        if (textureId == 0)
        {
            ImGui.TextUnformatted(status.Length == 0 ? "Loading character preview..." : status);
            return;
        }

        var width = Math.Min(ImGui.GetContentRegionAvail().X, 380 * ImGui.GetIO().FontGlobalScale * aspect);
        ImGui.Image(new ImTextureID((ulong)textureId), new Vector2(width, width / aspect));
        if (ImGui.Button("Rotate left")) rotate -= 20;
        ImGui.SameLine();
        if (ImGui.Button("Rotate right")) rotate += 20;
        if (ImGui.Button("Zoom in")) zoomDelta -= 1;
        ImGui.SameLine();
        if (ImGui.Button("Zoom out")) zoomDelta += 1;
    }

    private void Update(IFramework _)
    {
        if (!requested) { Release(); return; }
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
            var space = IMemorySpace.GetUISpace();
            var memory = space == null ? null : (CharaView*)space->Malloc((ulong)sizeof(CharaView), 16);
            if (memory == null) { Stop("Could not allocate the character preview."); return; }
            view = memory->Ctor();
            view->ModelData.CopyFromCharacter(source);
            view->Initialize(null, Slot, 0);
            player = actor.Address;
            lastImageOrStart = Environment.TickCount64;
            lastProgress = null;
        }

        var native = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (native != null) view->Render(native->FrameCounter);
        var stage = "Waiting for the native character renderer.";
        if (view->State == 6)
        {
            var character = view->GetCharacter();
            var hasModel = character != null && character->GetCharacterBase() != null;
            if (!cameraReady && view->Camera != null && hasModel)
            {
                view->ResetPositions();
                cameraReady = true;
            }
            if (cameraReady && rotate != 0) view->SetCameraYawAndPitch(rotate, 0);
            rotate = 0;
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
        if (view == null) return;
        textureId = 0;
        zoomDelta = 0;
        rotate = 0;
        cameraReady = false;
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
