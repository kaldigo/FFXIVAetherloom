using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class PreviewOutfit
{
    // Native equipment model order, followed separately by the two weapon slots.
    private static readonly string[] Armor = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];
    public static string Apply(ref CharaViewModelData model, JObject gear, JObject look, IDataManager data,JObject? priorBase=null)
    {
        model.EquipmentModelIds.Clear();
        model.WeaponModelIds.Clear();
        model.GlassesIds.Clear();
        model.HeadgearHidden=false;
        model.WeaponHidden=false;
        model.VisorClosed=false;
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
                {
                    replacement=item.ModelMain;
                    if(offhand && item.EquipSlotCategory.Value.MainHand>0)
                    {
                        // Older fist weapons store the gauntlets in ModelSub. The left fist is the adjacent weapon set.
                        replacement=item.ItemUICategory.RowId==1 && item.ModelSub is >0 and <0x100000000
                            ? item.ModelMain+50 : item.ModelSub;
                    }
                }
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

        if (gear["VieraEars"] is JObject ears && ears.Value<bool>("Apply")) model.VieraEarsHidden = !ears.Value<bool>("Show");

        var bytes = model.CustomizeData.Data;
        var originalIdentity=(bytes[4],bytes[1],bytes[5]);
        WardrobeBase.RestorePreviewAppearance(bytes,priorBase);
        foreach (var (key, index, mask) in WardrobeBase.Appearance)
        {
            if(look[key] is not JObject field || !field.Value<bool>("Apply"))continue;
            var value=field.Value<byte>("Value");
            if (key == "Hairstyle" && value != bytes[index] && !SupportsHairstyle(data, bytes[4], bytes[1], bytes[0], bytes[5], value))
            {
                skipped.Add($"hairstyle {value} (unavailable for this character)");
                continue;
            }
            bytes[index] = (byte)((bytes[index] & ~mask) | (value & mask));
        }
        if(originalIdentity!=(bytes[4],bytes[1],bytes[5]) && !SupportsHairstyle(data,bytes[4],bytes[1],bytes[0],bytes[5],bytes[6]))
        {
            for(int style=1;style<=byte.MaxValue;style++)
                if(SupportsHairstyle(data,bytes[4],bytes[1],bytes[0],bytes[5],(byte)style)){bytes[6]=(byte)style;break;}
        }
        return skipped.Count == 0 ? "" : $"Kept your equipped appearance for {string.Join(", ", skipped)}.";
    }

    private static bool SupportsHairstyle(IDataManager data, byte tribe, byte sex, byte race, byte face, byte hairstyle)
    {
        if (tribe == 0 || sex > 1) return false;
        var rowId = (uint)((tribe - 1) * 2 + sex);
        if (!data.GetExcelSheet<HairMakeType>().TryGetRow(rowId, out var options)) return false;
        var features = data.GetExcelSheet<CharaMakeCustomize>();
        foreach (var menu in options.CharaMakeStruct)
        {
            if (menu.Customize != 6) continue;
            for (var i = 0; i < menu.SubMenuNum && i < menu.SubMenuParam.Count; i++)
            {
                var id = menu.SubMenuParam[i];
                if (id == uint.MaxValue) continue;
                if (!features.TryGetRow(id, out var feature))
                {
                    if (i == hairstyle) return true;
                    continue;
                }
                if (feature.FeatureID != hairstyle) continue;
                // Hrothgar hair can be face-specific; the sheet uses face IDs 5–8.
                if (race != 7 || feature.Unknown0 == 0 || feature.Unknown0 == (face <= 4 ? face + 4 : face)) return true;
            }
        }
        return false;
    }
}
