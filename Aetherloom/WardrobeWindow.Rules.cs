using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    private readonly WardrobeLocations locations;
    private JObject CharacterSettings
    {
        get
        {
            var key=ownership.Character.ToString();
            if(assignments[key] is JObject settings)return settings;
            var created=new JObject();assignments[key]=created;return created;
        }
    }
    private JObject Rules=>WardrobeAssignment.Rules(CharacterSettings);
    public bool AssignmentMode
    {
        get=>saved.Value<bool?>("AssignmentMode")??true;
        set {if(value==AssignmentMode)return;saved["AssignmentMode"]=value;ModeRevision++;Write();}
    }
    internal int ModeRevision {get;private set;}
    public IEnumerable<(string Key,string Name)> RuleOptions(string type)
    {
        var options=locations.Options(type);
        if(type!="Named area")return options;
        // Keep previously assigned walk-in areas selectable after a relog elsewhere.
        var savedAreas=Rules.Properties().Select(p=>p.Name.Split('|')[0]).Where(k=>k.StartsWith("area:")).Distinct();
        return options.Concat(savedAreas.Select(k=>(Key:k,Name:locations.Name(k)))).DistinctBy(o=>o.Key);
    }
    public Guid? RuleOwner(string key)=>WardrobeAssignment.Owner(Rules,key);
    public IEnumerable<string> DesignRules=>Rules.Properties().Where(p=>RuleOwner(p.Name)==Selected).Select(p=>p.Name);
    public string RuleLabel(string key)
    {
        var parts=key.Split('|');
        return parts.Length==2?locations.Name(parts[0])+" · "+(parts[1]=="any"?"Any job":locations.Name(parts[1])):key;
    }
    private (ulong Character,int Places) noteContext;
    private readonly Dictionary<string,string> ruleNotes=[];
    public string RuleNote(string key)
    {
        var context=(ownership.Character,locations.Revision);
        if(noteContext!=context){ruleNotes.Clear();noteContext=context;}
        if(!ruleNotes.TryGetValue(key,out var note))ruleNotes[key]=note=BuildRuleNote(key);
        return note;
    }
    private string BuildRuleNote(string key)
    {
        var parts=key.Split('|');
        if(parts.Length!=2 || Rules.Count==0)return "";
        var notes=new HashSet<string>();
        foreach(var places in locations.PreviewPlaces(parts[0]))
        foreach(var jobs in locations.PreviewJobs(parts[1]))
        {
            var previous=WardrobeAssignment.Previous(Rules,places,jobs,parts[1],library.ContainsKey);
            if(previous is {} p)notes.Add(library[p.Design].Name+" ("+RuleLabel(p.Key)+")");
        }
        return notes.Count switch{0=>"",1=>"Takes priority over "+notes.First()+".",_=>"Depending on location or job, takes priority over: "+string.Join("; ",notes.Order())+"."};
    }
    public bool SaveRule(string key,Guid? expected,bool remove)
    {
        if(!HasCharacter || !library.ContainsKey(Selected) || !WardrobeAssignment.Edit(Rules,key,Selected,expected,remove))return false;
        assignedJobTerms=null;Write();return true;
    }
    internal bool TryAssignment(uint job,uint territory,uint map,out WardrobeAssignment assignment)
    {
        assignment=default;
        if(!HasCharacter || job==0 || territory==0)return false;
        if(!AssignmentMode)
        {
            if(IsOpen && layout.Tab=="Assignments")locations.Current(territory,map);
            return false;
        }
        if(Rules.Count==0 && !HasAppliedFields)
        {
            if(IsOpen && layout.Tab=="Assignments")locations.Current(territory,map);
            assignment=new(ownership.Character,job,territory,Guid.Empty);
            return true;
        }
        if(Environment.TickCount64>=nextRefresh)Refresh();
        if(disconnected)return false;
        var places=locations.Current(territory,map);
        assignment=WardrobeAssignment.Resolve(CharacterSettings,ownership.Character,job,territory,library.ContainsKey,places,locations.JobChain(job))
            ?? new(ownership.Character,job,territory,Guid.Empty);
        return true;
    }
}
