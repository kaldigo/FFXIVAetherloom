using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class PreviewOutfit
{
    // Native equipment model order, followed separately by the two weapon slots.
    private static readonly string[] Armor = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];
    private static readonly (string Key, int Index, byte Mask)[] Appearance =
        [("Hairstyle", 6, 255), ("Highlights", 7, 128), ("HairColor", 10, 255), ("HighlightsColor", 11, 255),
         ("FacePaint", 24, 127), ("FacePaintReversed", 24, 128), ("FacePaintColor", 25, 255)];

    public static string Apply(ref CharaViewModelData model, JObject gear, JObject look, IDataManager data)
    {
        var skipped = new List<string>();
        var items = data.GetExcelSheet<Item>();
        for (var i = 0; i < Armor.Length + 2; i++)
        {
            var weapon = i >= Armor.Length;
            var offhand = i == Armor.Length + 1;
            var name = weapon ? (offhand ? "OffHand" : "MainHand") : Armor[i];
            if (gear[name] is not JObject slot) continue;
            ulong? replacement = null;
            if (slot.Value<bool>("Apply") && slot.Value<ulong?>("ItemId") is { } id)
            {
                // Glamourer encodes empty slots, NPC smallclothes and raw models as special IDs.
                if (id == 0 || id is <= uint.MaxValue - 128 and >= uint.MaxValue - 150
                    || id is <= uint.MaxValue - 384 and >= uint.MaxValue - 639)
                    replacement = 0;
                else if (!weapon && id is <= uint.MaxValue - 256 and >= uint.MaxValue - 278)
                    replacement = 9903 | (1UL << 16);
                else if ((id >> 48) == 1)
                    replacement = weapon ? id & 0xFFFFFFFFFFUL : (ushort)id | (((id >> 32) & 255) << 16);
                else if (id <= uint.MaxValue && items.TryGetRow((uint)id, out var item))
                    replacement = offhand && item.EquipSlotCategory.Value.MainHand > 0 ? item.ModelSub : item.ModelMain;
                else
                {
                    skipped.Add(name);
                    continue;
                }
            }
            var dye = slot.Value<bool>("ApplyStain");
            if (weapon)
            {
                ref var part = ref model.WeaponModelIds[i - Armor.Length];
                if (replacement is { } value)
                {
                    part.Id = (ushort)value;
                    part.Type = (ushort)(value >> 16);
                    part.Variant = (ushort)(value >> 32);
                }
                if (dye) (part.Stain0, part.Stain1) = (slot.Value<byte>("Stain"), slot.Value<byte>("Stain2"));
            }
            else
            {
                ref var part = ref model.EquipmentModelIds[i];
                if (replacement is { } value) (part.Id, part.Variant) = ((ushort)value, (byte)(value >> 16));
                if (dye) (part.Stain0, part.Stain1) = (slot.Value<byte>("Stain"), slot.Value<byte>("Stain2"));
            }
        }
        if (gear["Hat"] is JObject hat && hat.Value<bool>("Apply")) model.HeadgearHidden = !hat.Value<bool>("Show");
        if (gear["Weapon"] is JObject weaponState && weaponState.Value<bool>("Apply")) model.WeaponHidden = !weaponState.Value<bool>("Show");
        if (gear["Visor"] is JObject visor && visor.Value<bool>("Apply")) model.VisorClosed = visor.Value<bool>("IsToggled");

        var bytes = model.CustomizeData.Data;
        foreach (var (key, index, mask) in Appearance)
        {
            if (look[key] is not JObject field || !field.Value<bool>("Apply")) continue;
            if (key == "Hairstyle" && (look["Clan"]?.Value<byte>("Value") != bytes[4]
                || look["Gender"]?.Value<byte>("Value") != bytes[1] || look["Face"]?.Value<byte>("Value") != bytes[5]))
            {
                skipped.Add("hairstyle (different clan, gender or face)");
                continue;
            }
            bytes[index] = (byte)((bytes[index] & ~mask) | (field.Value<byte>("Value") & mask));
        }
        return skipped.Count == 0 ? "" : $"Kept your equipped appearance for {string.Join(", ", skipped)}.";
    }
}
