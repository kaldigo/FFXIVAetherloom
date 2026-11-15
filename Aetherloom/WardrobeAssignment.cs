using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal readonly record struct WardrobeAssignment(ulong Character,uint Job,uint Zone,Guid Design)
{
    // Both the automation and editor pass most-specific-first location and job chains.
    internal static (string Key,Guid Design)? Winner(JObject rules,IEnumerable<string> places,IEnumerable<string> jobs,Func<Guid,bool> visible)
    {
        foreach(var place in places.Distinct(StringComparer.Ordinal))
            foreach(var job in jobs)
            {
                var key=place+"|"+job;
                if(rules[key] is JValue v && Guid.TryParse(v.ToString(),out var id) && visible(id))return (key,id);
            }
        return null;
    }

    internal static (string Key,Guid Design)? Previous(JObject rules,string[] places,string[] jobs,string jobCondition,Func<Guid,bool> visible)
    {
        // At the proposed location only broader job conditions can be underneath it.
        // In a parent location even an exact-job rule falls below the proposed location.
        var broader=jobs.SkipWhile(job=>job!=jobCondition).Skip(1);
        return Winner(rules,places.Take(1),broader,visible) ?? Winner(rules,places.Skip(1),jobs,visible);
    }

    internal static Guid? Owner(JObject rules,string key)
        =>rules[key] is JValue v && Guid.TryParse(v.ToString(),out var id)?id:null;

    internal static bool Edit(JObject rules,string key,Guid design,Guid? expected,bool remove)
    {
        if(Owner(rules,key)!=expected || (remove && expected!=design))return false;
        if(remove)rules.Remove(key);else rules[key]=design.ToString();
        return true;
    }

    internal static JObject Rules(JObject character)
    {
        if(character["Rules"] is JObject existing)return existing;
        var rules=new JObject();
        foreach(var kind in new[]{"Jobs","Zones"})
        {
            if(character[kind] is not JObject legacy)continue;
            foreach(var entry in legacy.Properties())
            {
                if(!uint.TryParse(entry.Name,out var number) || number==0 || entry.Value is not JValue value
                    || !Guid.TryParse(value.ToString(),out var owner))continue;
                var key=kind=="Jobs"?"any|job:"+number:"territory:"+number+"|any";
                if(rules[key]==null)rules[key]=owner.ToString();
            }
        }
        character["Rules"]=rules;
        return rules;
    }

    internal static WardrobeAssignment? Resolve(JObject? bindings,ulong character,uint job,uint zone,Func<Guid,bool> visible,
        IEnumerable<string>? places=null,IEnumerable<string>? jobs=null)
    {
        if(bindings==null || character==0 || job==0 || zone==0)return null;
        var winner=Winner(Rules(bindings),places??["territory:"+zone,"any"],jobs??["job:"+job,"any"],visible);
        return winner is {} w?new(character,job,zone,w.Design):null;
    }
}
