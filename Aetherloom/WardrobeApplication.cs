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
    // Equipped inventory indices include the unused waist slot at index 5.
    private static readonly string[] EquippedSlots=["MainHand","OffHand","Head","Body","Hands","","Legs","Feet","Ears","Neck","Wrists","RFinger","LFinger"];
    internal static bool PlanRestoration(JObject previous,JObject payload,JObject currentGear,JObject currentLook,
        bool manual,Func<int,JObject?> equippedAt,IEnumerable<string> appearanceFields,out JObject next,out bool restoreLook)
    {
        next=manual?(JObject)previous.DeepClone():new JObject();
        restoreLook=false;
        var gear=(JObject)payload["Equipment"]!;
        for(int i=0;i<EquippedSlots.Length;i++)
        {
            string slot=EquippedSlots[i];if(slot=="")continue;
            var piece=gear[slot] as JObject;
            int applied=(piece?.Value<bool>("Apply")==true?1:0)|(piece?.Value<bool>("ApplyStain")==true?2:0);
            int old=previous.Value<int?>(slot)??0;
            int release=manual?0:old & ~applied;
            if(release!=0)
            {
                var equipped=equippedAt(i);if(equipped==null)return false;
                if(piece==null){piece=new JObject();gear[slot]=piece;}
                if((release&1)!=0){piece["ItemId"]=equipped["ItemId"];piece["Apply"]=true;}
                if((release&2)!=0)
                {
                    piece["ItemId"]??=currentGear[slot]?["ItemId"]?.DeepClone();
                    piece["Stain"]=equipped["Stain"];piece["Stain2"]=equipped["Stain2"];piece["ApplyStain"]=true;
                }
            }
            if(applied!=0)next[slot]=manual?old|applied:applied;
        }
        var look=(JObject)payload["Customize"]!;
        foreach(string field in appearanceFields.Concat(["Hat","Visor","Weapon","VieraEars"]))
        {
            var isLook=field is not ("Hat" or "Visor" or "Weapon" or "VieraEars");
            var section=isLook?look:gear;
            var attr=isLook?"Value":field=="Visor"?"IsToggled":"Show";
            if((isLook?currentLook:currentGear)[field] is not JObject current || current[attr] is not {} value)continue;
            var old=previous[field] as JObject;
            var unchanged=old!=null && JToken.DeepEquals(old["Applied"],value);
            if(section[field] is JObject target && target.Value<bool>("Apply") && target[attr] is {} applied)
                next[field]=new JObject{["Before"]=(unchanged?old!["Before"]:value)?.DeepClone(),["Applied"]=applied.DeepClone()};
            else if(!manual && unchanged)
            {
                section[field]=new JObject{[attr]=old!["Before"]?.DeepClone(),["Apply"]=true};
                restoreLook|=isLook;
            }
        }
        return true;
    }
}
