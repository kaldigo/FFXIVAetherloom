using System.Numerics;
using AetherglassUI;
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
    private (uint TL,uint TR,uint BL,uint BR)? pendingColors, installedColors;
    private long nextTextureAttempt;
    public Vector2 WindowOrigin, WindowSize;
    public bool WeaponDrawn { get; set; }
    private bool? appliedPose;
    private bool resetCamera;
    public void ResetCamera() => resetCamera=true;
    private readonly PreviewCameraInput cameraInput = new();
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

    public void Start(bool design) { requested=true; previewDesign=design; status=""; }

    public void Close()
    {
        cameraInput.EndDrag();
        requested = false;
        textureId = 0;
        status = "";
    }

    public void Options(bool hasDesign)
    {
        if (hasDesign && (!requested || !previewDesign))
        {
            if (ImGui.Button("Preview selected design"))
            {
                previewDesign = true;
                requested = true;
                status = "";
            }

        }
        if ((!requested || previewDesign) && ImGui.Button("Preview current gear"))
        {
            previewDesign = false;
            requested = true;
            status = "";
        }
        if(requested && ImGui.Button("Stop preview"))Close();
    }

    public void Draw(JObject? design)
    {
        lastDraw=Environment.TickCount64;
        selectedDesign=design;
        if (!requested || textureId==0 || !ReferenceEquals(loadedDesign,previewDesign?design:null))
        {
            ImGui.TextWrapped(status.Length>0?status:requested?"Loading character preview...":"Preview stopped.");
            return;
        }
        WorkspaceControls.FillViewport("##character-camera",new Picture(new ImTextureID((ulong)textureId)),aspect,(tl,br)=>
        {
            var io=ImGui.GetIO();
            if(ImGui.IsItemHovered()) { ImGuiP.SetItemUsingMouseWheel(); zoomDelta-=io.MouseWheel; }
            cameraInput.Read(io.FontGlobalScale,ref rotate,ref pan);
            if (WindowSize.X>0 && WindowSize.Y>0)
            {
                uint Sample(Vector2 p)
                {
                    var c=WindowShell.BackdropColor(Vector2.Clamp((p-WindowOrigin)/WindowSize,Vector2.Zero,Vector2.One));
                    return 0xff000000u|((uint)c.X<<16)|((uint)c.Y<<8)|(uint)c.Z;
                }
                pendingColors=(Sample(tl),Sample(new(br.X,tl.Y)),Sample(new(tl.X,br.Y)),Sample(br));
            }
        });
        if(notes.Length>0 && ImGui.IsItemHovered())ImGui.SetTooltip(notes);
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
            if ((!cameraReady || resetCamera) && view->Camera != null && hasModel)
            {
                view->ResetPositions();
                cameraReady = true;
                resetCamera=false;
                pan=rotate=Vector2.Zero;zoomDelta=0;
            }
            if(cameraReady && hasModel && character->IsReadyToDraw() && appliedPose!=WeaponDrawn)
            {
                view->ToggleDrawWeapon(WeaponDrawn);
                appliedPose=WeaponDrawn;
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
        var renderer=OffscreenRenderingManager.Instance();
        if(renderer==null || pendingColors is not {} colors || (renderer==backgroundOwner && installedColors==colors))return;
        if(Environment.TickCount64<nextTextureAttempt)return;
        nextTextureAttempt=Environment.TickCount64+250;
        const int size=32;
        var texture=Texture.CreateTexture2D(size,size,1,TextureFormat.B8G8R8A8_UNORM,
            TextureFlags.TextureType2D|TextureFlags.Managed|TextureFlags.Immutable,7);
        if(texture==null)return;
        uint* pixels=stackalloc uint[size*size];
        static Vector3 Unpack(uint color)=>new((color>>16)&255,(color>>8)&255,color&255);
        for(int y=0;y<size;y++)
        {
            float v=(y+.5f)/size;
            var left=Vector3.Lerp(Unpack(colors.TL),Unpack(colors.BL),v);
            var right=Vector3.Lerp(Unpack(colors.TR),Unpack(colors.BR),v);
            for(int x=0;x<size;x++)
            {
                var c=Vector3.Lerp(left,right,(x+.5f)/size);
                pixels[y*size+x]=0xff000000u|((uint)MathF.Round(c.X)<<16)|((uint)MathF.Round(c.Y)<<8)|(uint)MathF.Round(c.Z);
            }
        }
        if(!texture->InitializeContents(pixels)){texture->DecRef();return;}
        RestoreBackground();
        // Transfer our texture reference to the slot and keep its previous reference for restoration.
        previousBackground = renderer->BackgroundTextures[(int)Slot];
        renderer->BackgroundTextures[(int)Slot] = texture;
        installedBackground = texture;
        backgroundOwner = renderer;
        installedColors=colors;
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
        installedColors=null;
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
        cameraInput.EndDrag();
        if (view == null) return;
        textureId = 0;
        zoomDelta = 0;
        rotate = pan = Vector2.Zero;
        cameraReady = false;
        appliedPose=null;
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
