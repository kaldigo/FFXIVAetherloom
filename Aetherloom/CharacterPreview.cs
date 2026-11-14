using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed unsafe class CharacterPreview : IDisposable
{
    // The eighth portrait target is shared with the game's party portraits.
    private const uint Slot = 7;
    private const int ActorIndex = 440 + (int)Slot;
    private static readonly string?[] Slots = ["MainHand", "OffHand", "Head", "Body", "Hands", null, "Legs",
        "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];
    private static readonly (string Key, int Offset, byte Mask)[] Appearance =
        [("Hairstyle", 6, 255), ("Highlights", 7, 128), ("HairColor", 10, 255), ("HighlightsColor", 11, 255),
         ("FacePaint", 24, 127), ("FacePaintReversed", 24, 128), ("FacePaintColor", 25, 255)];

    private readonly IFramework framework;
    private readonly IPartyList party;
    private readonly ICondition conditions;
    private readonly IObjectTable objects;
    private readonly IAddonLifecycle addons;
    private CharaViewPortrait* portrait;
    private JObject? requested, loaded;
    private nint player, textureId;
    private float aspect, rotate;
    private int zoom = 60;
    private long started, lastDraw;
    private bool cameraReady;
    private string status = "";

    public CharacterPreview(IFramework framework, IPartyList party, ICondition conditions, IObjectTable objects, IAddonLifecycle addons)
    {
        (this.framework, this.party, this.conditions, this.objects, this.addons) = (framework, party, conditions, objects, addons);
        framework.Update += Update;
        addons.RegisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
    }

    public void Close()
    {
        requested = null;
        textureId = 0;
        status = "";
    }

    public void Draw(JObject design)
    {
        lastDraw = Environment.TickCount64;
        if (requested == null)
        {
            if (ImGui.Button("Start embedded preview"))
                requested = design;
            if (status.Length != 0) ImGui.TextWrapped(status);
            return;
        }

        requested = design;
        if (ImGui.Button("Stop preview")) { Close(); return; }
        if (textureId == 0 || loaded != design)
        {
            ImGui.TextUnformatted("Loading character preview...");
            return;
        }

        var width = Math.Min(ImGui.GetContentRegionAvail().X, 380 * ImGui.GetIO().FontGlobalScale * aspect);
        ImGui.Image(new ImTextureID((ulong)textureId), new Vector2(width, width / aspect));
        if (ImGui.Button("Rotate left")) rotate -= 0.25f;
        ImGui.SameLine();
        if (ImGui.Button("Rotate right")) rotate += 0.25f;
        ImGui.SliderInt("Zoom", ref zoom, 0, 200);
    }

    private void Update(IFramework _)
    {
        var design = requested;
        if (design == null) { Release(); return; }
        if (objects.LocalPlayer is not { } actor || party.Length != 0 || conditions[ConditionFlag.BoundByDuty]
            || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || (portrait != null && actor.Address != player) || Environment.TickCount64 - lastDraw > 3000)
        {
            Stop("Start preview while solo outside a duty, after the area has loaded.");
            return;
        }

        if (portrait != null && !ReferenceEquals(design, loaded)) { Release(); return; }
        if (portrait == null)
        {
            if (objects[ActorIndex] != null) { Stop("The portrait slot is in use. Close party portraits and try again."); return; }
            if (design["Equipment"] is not JObject gear || design["Customize"] is not JObject look
                || look.Value<uint?>("ModelId").GetValueOrDefault() != 0)
            {
                Stop("Select a player-character design for this preview.");
                return;
            }
            CharaViewCharacterData data = default;
            data.Ctor();
            data.ImportLocalPlayerEquipment();
            ApplyAppearance(ref data, gear, look);
            var space = IMemorySpace.GetUISpace();
            var memory = space == null ? null : (CharaViewPortrait*)space->Malloc((ulong)sizeof(CharaViewPortrait), 16);
            if (memory == null) { Stop("Could not allocate the character preview."); return; }
            portrait = memory->Ctor();
            portrait->Setup(Slot, &data, null, 0, 0);
            portrait->ToggleCharacterVisibility(true);
            player = actor.Address;
            loaded = design;
            started = Environment.TickCount64;
        }

        var native = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        if (native != null) portrait->Render(native->FrameCounter);
        if (portrait->State == 6 && portrait->CharacterLoaded)
        {
            if (!cameraReady)
            {
                portrait->ResetCamera();
                portrait->SetCameraZoom((byte)zoom);
                portrait->SetAmbientLightingColor(255, 255, 255);
                portrait->SetAmbientLightingBrightness(70);
                portrait->SetDirectionalLightingColor(255, 255, 255);
                portrait->SetDirectionalLightingBrightness(80);
                cameraReady = true;
            }
            if (rotate != 0) portrait->SetCameraYawAndPitch(rotate, 0);
            rotate = 0;
            if (portrait->CameraZoom != zoom) portrait->SetCameraZoom((byte)zoom);
            var targets = RenderTargetManager.Instance();
            var texture = targets == null ? null : targets->GetCharaViewTexture(Slot);
            if (texture != null && texture->D3D11ShaderResourceView != null && texture->ActualWidth > 0 && texture->ActualHeight > 0)
            {
                aspect = (float)texture->ActualWidth / texture->ActualHeight;
                textureId = (nint)texture->D3D11ShaderResourceView;
                return;
            }
        }
        textureId = 0;
        if (Environment.TickCount64 - started > 15000)
            Stop($"The preview did not produce an image (portrait state {portrait->State}).");
    }

    private static void ApplyAppearance(ref CharaViewCharacterData data, JObject gear, JObject look)
    {
        for (var i = 0; i < Slots.Length; i++)
        {
            if (Slots[i] is not { } slot || gear[slot] is not JObject item) continue;
            if (item.Value<bool>("Apply") && item.Value<ulong?>("ItemId") is { } id)
                data.ItemIds[i] = id < 1_000_000 ? (uint)id : 0;
            if (item.Value<bool>("ApplyStain"))
            {
                data.ItemStain0Ids[i] = item.Value<byte>("Stain");
                data.ItemStain1Ids[i] = item.Value<byte>("Stain2");
            }
        }

        var bytes = data.CustomizeData.Data;
        foreach (var (key, offset, mask) in Appearance)
        {
            if (look[key] is not JObject field || !field.Value<bool>("Apply")) continue;
            // A style made for a different clan, sex or face may have no model for this player.
            if (key == "Hairstyle" && (look["Clan"]?.Value<byte>("Value") != bytes[4]
                || look["Gender"]?.Value<byte>("Value") != bytes[1] || look["Face"]?.Value<byte>("Value") != bytes[5])) continue;
            bytes[offset] = (byte)((bytes[offset] & ~mask) | (field.Value<byte>("Value") & mask));
        }
    }

    private void OnPartyPortraits(AddonEvent evt, AddonArgs args)
    {
        if (requested != null || portrait != null) Stop("The game is using its party portraits.");
    }

    private void Stop(string reason)
    {
        Close();
        status = reason;
        Release();
    }

    private void Release()
    {
        if (portrait == null) return;
        textureId = 0;
        loaded = null;
        rotate = 0;
        cameraReady = false;
        portrait->Release();
        portrait->Dtor(1);
        portrait = null;
    }

    public void Dispose()
    {
        framework.Update -= Update;
        addons.UnregisterListener(AddonEvent.PreSetup, "BannerParty", OnPartyPortraits);
        Close();
        if (portrait != null) framework.RunOnFrameworkThread(Release).GetAwaiter().GetResult();
    }
}
