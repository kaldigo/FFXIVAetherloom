using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    private readonly Dictionary<Guid,(int Mask,long RetryAfter)> wearableGear=[];
    private readonly Dictionary<uint,int> itemMasks=[];
    private (byte Race,byte Sex)? visibleAppearance;
    private int remoteReadsRemaining;

    private void BeginWearableFrame()
    {
        visibleAppearance=null;
        remoteReadsRemaining=4;
    }

    public unsafe bool Wearable(Guid id)
    {
        if(visibleAppearance==null)
        {
            if(objects.LocalPlayer is not {} player)return false;
            var appearance=CharacterPreview.VisibleCustomize((Character*)player.Address);
            visibleAppearance=(appearance.Race,appearance.Sex);
        }
        if(!wearableGear.TryGetValue(id,out var cached) || Environment.TickCount64>=cached.RetryAfter)
        {
            var plan=Plans[id.ToString()] as JObject;
            if(plan==null)
            {
                if(disconnected || remoteReadsRemaining<=0)return false;
                remoteReadsRemaining--;
                try { plan=plugin.GetIpcSubscriber<Guid,JObject?>("Glamourer.GetDesignJObject").InvokeFunc(id); }
                catch(IpcError){plan=null;}
            }
            cached=plan==null ? (0,Environment.TickCount64+2000)
                : (WardrobeWearability.DesignMask(plan,Gear.Concat(Accessories),ItemMask),long.MaxValue);
            wearableGear[id]=cached;
        }
        return WardrobeWearability.Allows(cached.Mask,visibleAppearance.Value.Race,visibleAppearance.Value.Sex);
    }

    private int ItemMask(ulong id)
    {
        if(IsEmptyItem(id))return WardrobeWearability.Everyone;
        if(id>uint.MaxValue)return 0;
        uint item=(uint)id;
        if(itemMasks.TryGetValue(item,out var mask))return mask;
        mask=0;
        if(data.GetExcelSheet<Item>().TryGetRow(item,out var row))
        {
            if(row.EquipRestriction.RowId==0)mask=WardrobeWearability.Everyone;
            else if(row.EquipRestriction.ValueNullable is {} rule)
                mask=(rule.Hyur?1:0)|(rule.Elezen?2:0)|(rule.Lalafell?4:0)|(rule.Miqote?8:0)
                    |(rule.Roegadyn?16:0)|(rule.AuRa?32:0)|(rule.Hrothgar?64:0)|(rule.Viera?128:0)
                    |(rule.Male?256:0)|(rule.Female?512:0);
        }
        return itemMasks[item]=mask;
    }
}
