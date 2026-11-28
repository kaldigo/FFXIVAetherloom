using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeLocationPolicy
{
    internal static string[] PropertyChain(string property)=>property switch
    {
        "personal" or "apartment"=>[property,"mine","properties"],
        "fc" or "chambers"=>[property,"fc-properties","properties"],
        "mine" or "fc-properties"=>[property,"properties"],
        "properties"=>[property],
        _=>[],
    };

    internal static bool FieldOperation(uint use)=>use is 41 or 48 or 61;

    // All characters migrate together. Removed or conflicting rules are retained for review.
    internal static bool Migrate(JObject assignments,Func<string,string?> canonical)
    {
        bool changed=false;
        foreach(var settings in assignments.Properties().Select(p=>p.Value).OfType<JObject>())
        {
            if(settings.Value<int?>("LocationSchema")==2)continue;
            var old=WardrobeAssignment.Rules(settings);
            var rules=new JObject();
            var review=(JObject?)settings["ReviewRules"]??new();
            var entries=old.Properties().Select(p=>
            {
                var parts=p.Name.Split('|');
                var place=parts.Length==2?canonical(parts[0]):null;
                return (Rule:p,Target:place==null?null:place+"|"+parts[1]);
            }).OrderBy(p=>p.Target==p.Rule.Name?0:1).ThenBy(p=>p.Rule.Name,StringComparer.Ordinal).ToArray();
            var ambiguous=entries.Where(p=>p.Target!=null).GroupBy(p=>p.Target!)
                .Where(g=>g.All(p=>p.Rule.Name!=g.Key) && g.Select(p=>p.Rule.Value.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)
                .Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
            foreach(var (rule,target) in entries)
            {
                string? reason=target==null?"This location was removed. Choose a replacement below.":null;
                if(target!=null && (ambiguous.Contains(target) || rules[target]!=null && !string.Equals(rules[target]!.ToString(),rule.Value.ToString(),StringComparison.OrdinalIgnoreCase)))
                    reason="Several old assignments now share one location. Review the active assignment before choosing a replacement.";
                if(reason!=null)
                    review[rule.Name]=new JObject{["Design"]=rule.Value.DeepClone(),["Reason"]=reason};
                else rules[target!]=rule.Value.DeepClone();
            }
            settings["Rules"]=rules;
            settings["ReviewRules"]=review;
            settings["LocationSchema"]=2;
            changed=true;
        }
        return changed;
    }
}
