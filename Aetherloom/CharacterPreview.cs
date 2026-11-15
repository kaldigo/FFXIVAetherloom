using System.Numerics;
using AetherglassUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures.TextureWraps;
using System.Threading.Tasks;
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
    private readonly ITextureProvider textures;
    private Task<IDalamudTextureWrap>? copying;
    private IDalamudTextureWrap? still;
    private int width,height;
    private CharaViewModelData baseline,loadedModel;
    private long nextBaselineCheck;
    private bool firstFrame;
    private readonly List<(Vector2 Rotate,Vector2 Pan,float Zoom)> cameraMoves=[];
    // The renderer owns this handle. Dalamud takes its own reference when it starts the copy.
    private sealed record RenderImage(ImTextureID Handle,int Width,int Height):IDalamudTextureWrap
    {
        public void Dispose(){}
    }
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

    public CharacterPreview(IFramework framework, IGameGui gameGui, ICondition conditions, IObjectTable objects, IAddonLifecycle addons, IPluginLog log, IDataManager data,ITextureProvider textures)
    {
        (this.framework, this.gameGui, this.conditions, this.objects, this.addons) = (framework, gameGui, conditions, objects, addons);
        this.log = log;
        this.data = data;
        this.textures=textures;
        framework.Update += Update;
        addons.RegisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
    }

    public void Start() { cameraMoves.Clear();resetCamera=true; requested=true; status=""; }

    public void Close()
    {
        cameraInput.EndDrag();
        requested = false;
        selectedDesign=null;
        textureId = 0;
        DiscardStill();
        status = "";
    }

    public void Draw(JObject? design)
    {
        lastDraw=Environment.TickCount64;
        if(!ReferenceEquals(selectedDesign,design)){requested=design!=null;status="";}
        selectedDesign=design;
        if(design==null)return;
        var image=still?.Handle??new ImTextureID((ulong)textureId);
        if (!requested || (textureId==0 && still==null))
        {
            ImGui.TextWrapped(status.Length>0?status:requested?"Loading character preview...":"Preview stopped.");
            return;
        }
        WorkspaceControls.FillViewport("##character-camera",new Picture(image),aspect,(tl,br)=>
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
        var desired=selectedDesign;
        if(desired==null){DiscardStill();Release();return;}

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
            DiscardStill();Release();
            return;
        }
        var source=(Character*)actor.Address;
        var now=Environment.TickCount64;
        bool changed=!ReferenceEquals(desired,loadedDesign);
        if(view==null || changed || now>=nextBaselineCheck || copying!=null)
        {
            if(source->GetCharacterBase()==null || !source->IsReadyToDraw())
            {
                if(view==null)status="Waiting for your visible character model.";
                return;
            }
            var current=ReadBaseline(source);
            nextBaselineCheck=now+250;
            if(view==null || changed || !SameModel(ref current,ref baseline) || copying!=null)
            {
                var model=current;
                notes="";
                if(desired!=null)
                {
                    if(desired["Equipment"] is not JObject gear || desired["Customize"] is not JObject look || look.Value<uint>("ModelId")!=0)
                    { Stop("Select a player-character design to preview.");return; }
                    notes=PreviewOutfit.Apply(ref model,gear,look,data);
                }
                if(view==null || !SameModel(ref model,ref loadedModel))
                {
                    if(view!=null)
                    {
                        if(!HoldLastFrame())return;
                        Release();
                    }
                    if(objects[ActorIndex]!=null){status="Waiting for the preview slot to become available.";return;}
                    if(!Create(model,actor.Address))return;
                    log.Debug($"Preview model: base race {source->DrawData.CustomizeData.Race}, visible race {current.CustomizeData.Race}, target race {model.CustomizeData.Race}, retained frame {still!=null}, camera inputs {cameraMoves.Count}.");
                }
                else if(copying!=null)DiscardStill();
                baseline=current;
                loadedDesign=desired;
            }
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
                if(resetCamera){cameraMoves.Clear();pan=rotate=Vector2.Zero;zoomDelta=0;}
                else foreach(var move in cameraMoves)MoveCamera(move.Rotate,move.Pan,move.Zoom);
                resetCamera=false;
            }
            if(cameraReady && hasModel && character->IsReadyToDraw() && appliedPose!=WeaponDrawn)
            {
                view->ToggleDrawWeapon(WeaponDrawn);
                appliedPose=WeaponDrawn;
            }
            if(cameraReady && (rotate!=Vector2.Zero || pan!=Vector2.Zero || zoomDelta!=0))
            {
                MoveCamera(rotate,pan,zoomDelta);
                cameraMoves.Add((rotate,pan,zoomDelta));
                rotate=pan=Vector2.Zero;zoomDelta=0;
            }
            var targets = RenderTargetManager.Instance();
            var texture = targets == null ? null : targets->GetCharaViewTexture(Slot);
            if (cameraReady && hasModel && character->IsReadyToDraw()
                && texture != null && texture->D3D11ShaderResourceView != null && texture->ActualWidth > 0 && texture->ActualHeight > 0)
            {
                if(firstFrame){firstFrame=false;return;} // Render once with the restored camera before showing the replacement.
                width=(int)texture->ActualWidth;height=(int)texture->ActualHeight;
                aspect=(float)width/height;
                still?.Dispose();still=null;
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

    private void MoveCamera(Vector2 turn,Vector2 offset,float zoom)
    {
        if(turn!=Vector2.Zero)view->SetCameraYawAndPitch(turn.X,turn.Y);
        if(offset!=Vector2.Zero)view->SetCameraXAndY(offset.X,offset.Y);
        if(zoom!=0)view->SetCameraDistance(zoom);
    }

    internal static CustomizeData VisibleCustomize(Character* character)
    {
        var draw=character->GetCharacterBase();
        return draw!=null && draw->GetModelType()==CharacterBase.ModelType.Human
            ? ((Human*)draw)->Customize : character->DrawData.CustomizeData;
    }

    private static CharaViewModelData ReadBaseline(Character* source)
    {
        CharaViewModelData result=default;
        result.CopyFromCharacter(source);
        var draw=source->GetCharacterBase();
        if(draw->GetModelType()!=CharacterBase.ModelType.Human)return result;
        var human=(Human*)draw;
        result.CustomizeData=human->Customize;
        human->EquipmentModels[..10].CopyTo(result.EquipmentModelIds);
        // Draw objects contain the weapon models actually shown after Glamourer overrides.
        for(int i=0;i<2;i++)
        {
            var weapon=source->DrawData.Weapon((DrawDataContainer.WeaponSlot)i).DrawData.DrawObject;
            if(weapon==null || weapon->Object.GetObjectType()!=FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase
                || ((CharacterBase*)weapon)->GetModelType()!=CharacterBase.ModelType.Weapon)continue;
            var visible=(Weapon*)weapon;
            ref var id=ref result.WeaponModelIds[i];
            id.Id=visible->ModelSetId;id.Type=visible->SecondaryId;id.Variant=visible->Variant;
            id.Stain0=visible->Stain0;id.Stain1=visible->Stain1;
        }
        result.VisorClosed=human->VisorToggled;
        result.VieraEarsHidden=human->HideVieraEars;
        return result;
    }

    private static bool SameModel(ref CharaViewModelData a,ref CharaViewModelData b)
        => a.CustomizeData.Data.SequenceEqual(b.CustomizeData.Data)
           && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.EquipmentModelIds).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.EquipmentModelIds))
           && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.WeaponModelIds).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.WeaponModelIds))
           && a.HeadgearHidden==b.HeadgearHidden && a.WeaponHidden==b.WeaponHidden && a.VisorClosed==b.VisorClosed && a.VieraEarsHidden==b.VieraEarsHidden;

    private bool Create(CharaViewModelData model,nint actor)
    {
        var space=IMemorySpace.GetUISpace();
        var memory=space==null?null:(CharaView*)space->Malloc((ulong)sizeof(CharaView),16);
        if(memory==null){Stop("Could not allocate the character preview.");return false;}
        view=memory->Ctor();
        loadedModel=model;view->ModelData=model;
        view->Initialize(null,Slot,0);
        player=actor;lastImageOrStart=Environment.TickCount64;lastProgress=null;firstFrame=true;
        return true;
    }

    private bool HoldLastFrame()
    {
        if(still!=null || textureId==0 && copying==null)return true;
        try
        {
            copying??=textures.CreateFromExistingTextureAsync(new RenderImage(new ImTextureID((ulong)textureId),width,height),debugName:"Aetherloom previous preview");
            if(!copying.IsCompleted)return false;
            still=copying.GetAwaiter().GetResult();
        }
        catch(Exception error){log.Warning(error,"Could not retain the previous preview image.");}
        copying=null;
        return true;
    }

    private void DiscardStill()
    {
        still?.Dispose();still=null;
        if(copying is not {} task)return;
        copying=null;
        _=task.ContinueWith(t=>{
            if(t.IsCompletedSuccessfully)t.Result.Dispose();
            else if(t.IsFaulted)log.Debug(t.Exception!,"Preview frame copy ended after the preview closed.");
        },TaskScheduler.Default);
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
        DiscardStill();
        Release();
    }

    private void Stop(string reason)
    {
        log.Warning(reason);
        var failed=selectedDesign;
        Close();selectedDesign=failed;
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
