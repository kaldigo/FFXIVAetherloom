using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeApplication
{
    internal static JObject BuildPlan(JObject currentGear,JObject currentLook,JObject plan,
        IEnumerable<string> slots,IEnumerable<string> fields,JObject? main,Func<ulong,bool> eligible,
        List<string> skipped,out bool applyLook)
    {
        var equipment=(JObject)plan["Equipment"]!;
        var appearance=(JObject)plan["Customize"]!;
        bool bypass=plan.Value<bool>("BypassOwnership");
        var gear=new JObject();
        foreach(var key in slots)
        {
            var slot=key=="MainHand" ? main : equipment[key] as JObject;
            // Glamourer validates the two weapons together, even when just one is applied.
            if(key is "MainHand" or "OffHand" && currentGear[key] is JObject weapon)
                gear[key]=new JObject { ["ItemId"]=weapon["ItemId"]!.DeepClone() };
            if(slot==null || !(slot.Value<bool>("Apply") || slot.Value<bool>("ApplyStain")))continue;
            var id=slot.Value<ulong>("ItemId");
            if(!bypass && !eligible(id)){skipped.Add(key);continue;}
            var item=(JObject)slot.DeepClone();
            item.Remove("ApplyCrest");
            gear[key]=item;
        }
        foreach(var key in new[]{"Hat","Visor","Weapon","VieraEars"})
            if(equipment[key]?.Value<bool>("Apply")==true)gear[key]=equipment[key]!.DeepClone();

        var look=(JObject)currentLook.DeepClone();
        foreach(var field in look.Properties().Select(p=>p.Value).OfType<JObject>())field.Remove("Apply");
        applyLook=false;
        foreach(var key in fields)
            if(appearance[key]?.Value<bool>("Apply")==true){look[key]=appearance[key]!.DeepClone();applyLook=true;}
        return new JObject { ["FileVersion"]=1, ["Customize"]=look, ["Equipment"]=gear };
    }
}
