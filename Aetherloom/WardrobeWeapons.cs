using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeWeapons
{
    // ItemUICategory rows: shields, secondary tools for DoH/MIN/BTN, and FSH spearfishing gigs.
    internal static uint OffCategory(uint mainCategory)=>mainCategory switch
    {
        2 or 6 or 8 or 113 => 11,
        >=12 and <=30 when mainCategory%2==0 => mainCategory+1,
        32 => 99,
        _ => 0
    };

    internal static IEnumerable<(string Key,JObject Slot)> Hands(JObject plan)
    {
        if(plan["Equipment"]?["MainHand"] is JObject main)yield return ("MainHand",main);
        if(plan["AdditionalMainHands"] is JObject extras)
            foreach(var p in extras.Properties())if(p.Value is JObject slot)yield return (p.Name,slot);
    }

    // Glamourer ItemManager.NothingId(FullEquipType.Shield), used by ApplyState.
    internal const uint EmptyShield=uint.MaxValue-384-37;

    internal static JObject? OffHand(JObject plan,string key,JObject main,uint category,bool builtIn)
    {
        if(OffCategory(category)==0)return builtIn?(JObject)main.DeepClone():null;
        var off=plan["PairedOffHands"]?[key] is JObject stored?(JObject)stored.DeepClone():Empty();
        if(IsEmptyItem(off.Value<ulong>("ItemId")))
        {
            off["ItemId"]=OffCategory(category)==11?EmptyShield:0u;
            off["Apply"]??=true;
            off["ApplyStain"]??=true;
        }
        return off;
    }

    internal static bool IsEmptyItem(ulong id)=>id==0 || id is <=uint.MaxValue-128 and >=uint.MaxValue-150
        || id is <=uint.MaxValue-384 and >=uint.MaxValue-639;

    internal static JObject Empty()=>new(){["ItemId"]=0,["Apply"]=true,["ApplyStain"]=true};

    internal static void Upgrade(JObject plan,Func<ulong,ulong,bool> compatible)
    {
        if(plan["PairedOffHands"]!=null)return;
        var pairs=new JObject();
        if(plan["Equipment"] is JObject gear && gear["OffHand"] is JObject old)
        {
            var id=old.Value<ulong>("ItemId");
            foreach(var (key,slot) in Hands(plan))
                if(compatible(slot.Value<ulong>("ItemId"),id))pairs[key]=old.DeepClone();
            // Keep a standalone legacy off hand for recovery when there is no compatible weapon.
            if(pairs.Count!=0)gear.Remove("OffHand");
        }
        plan["PairedOffHands"]=pairs;
    }
}
