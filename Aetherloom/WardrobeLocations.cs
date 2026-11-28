using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GameMap=Lumina.Excel.Sheets.Map;

namespace Aetherloom;

internal sealed class WardrobeLocations(ExcelSheet<ClassJob> jobSheet,ExcelSheet<TerritoryType> territories,ExcelSheet<GameMap> maps,ExcelSheet<PlaceName> names)
{
    internal WardrobeLocations(IDataManager data):this(data.GetExcelSheet<ClassJob>(),data.GetExcelSheet<TerritoryType>(),data.GetExcelSheet<GameMap>(),data.GetExcelSheet<PlaceName>()){}
    private readonly Dictionary<string,string> labels=new(){["any"]="Anywhere",["inn"]="All inns",["district"]="Residential districts",["inside"]="All interiors",["personal"]="My house",["fc"]="My FC estate",["apartment"]="My apartment",["chambers"]="My private chambers",["housing"]="All housing",["properties"]="My and FC properties",["mine"]="My properties",["fc-properties"]="My FC properties"};
    private readonly Dictionary<string,HashSet<string>> lists=[];
    private readonly Dictionary<string,string[]> chains=[];
    private readonly Dictionary<uint,string[]> jobs=[];
    internal int Revision {get;private set;}
    private readonly Dictionary<string,(string Key,string Name)[]> optionCache=[];
    private readonly Dictionary<uint,string> cityNames=[];
    private readonly Dictionary<string,string> aliases=[];
    private static readonly string[] RoleNames=["Tanks","Melee DPS","Ranged DPS","Healers","Caster DPS","Crafters","Gatherers"];

    internal string[] JobChain(uint id){Ensure();return jobs.GetValueOrDefault(id)??["job:"+id,"any"];}
    internal string Name(string key)
    {
        Ensure();
        if(labels.TryGetValue(key,out var name))return name;
        var parts=key.Split(':');
        if(parts is ["area",_,var raw] && uint.TryParse(raw,out var id) && names.TryGetRow(id,out var row))return row.Name.ExtractText();
        if(parts is ["map",var m] && uint.TryParse(m,out var mid) && maps.TryGetRow(mid,out var mr))return mr.PlaceNameSub.Value.Name.ExtractText();
        if(parts is ["territory",var t] && uint.TryParse(t,out var zone) && territories.TryGetRow(zone,out var territory))return territory.PlaceName.Value.Name.ExtractText();
        return key;
    }
    internal IEnumerable<(string Key,string Name)> Options(string type)
    {
        Ensure();
        if(optionCache.TryGetValue(type,out var cached))return cached;
        if(!lists.TryGetValue(type,out var keys))return [];
        // Labels are presentation data. Two different places may have the same localized name.
        return optionCache[type]=keys.GroupBy(k=>type=="Job" && k=="any"?"Any job":Name(k),StringComparer.OrdinalIgnoreCase).SelectMany(g=>g.Select(k=>(Key:k,Name:g.Count()>1?$"{g.Key} ({k})":g.Key)))
            .OrderBy(x=>x.Key is "any" or "cities"?0:1).ThenBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private void Add(string type,string key,string name)
    {
        if(string.IsNullOrWhiteSpace(name))return;
        if(!lists.TryGetValue(type,out var keys))lists[type]=keys=[];
        if(keys.Add(key)){Revision++;optionCache.Remove(type);}
        labels.TryAdd(key,name);
    }
    private void Ensure()
    {
        if(lists.Count>0)return;
        Add("Anywhere","any","Anywhere");Add("Job","any","Any job");
        Add("Hub","cities","All hubs");
        foreach(var row in jobSheet.Where(j=>j.RowId>0))
        {
            var name=row.Name.ExtractText();
            if(name.Length==0)continue;
            var key="job:"+row.RowId;
            Add("Job",key,name+" ("+row.Abbreviation.ExtractText()+")");
            int role=row.RowId is >=8 and <=15?5:row.RowId is >=16 and <=18?6:row.Role switch
                {1=>0,2=>1,3 when row.PrimaryStat==4=>4,3=>2,4=>3,_=>-1};
            if(role>=0)Add("Job","role:"+role,RoleNames[role]);
            jobs[row.RowId]=role>=0?[key,"role:"+role,"any"]:[key,"any"];
        }
        var sheet=territories;
        var districts=sheet.Where(t=>t.IsInUse && t.TerritoryIntendedUse.RowId==13 && t.PlaceName.RowId!=0).ToArray();
        var districtNames=districts.Select(t=>t.PlaceName.RowId).ToHashSet();
        foreach(var t in sheet.Where(t=>t.IsInUse && t.TerritoryIntendedUse.RowId==0 && t.PlaceNameZone.RowId>0 && !districtNames.Contains(t.PlaceName.RowId)))
        {
            var title=t.PlaceNameZone.Value.Name.ExtractText();
            if(title==t.PlaceNameRegion.Value.Name.ExtractText())title=t.PlaceName.Value.Name.ExtractText();
            cityNames.TryAdd(t.PlaceNameZone.RowId,title);
        }
        if(sheet.TryGetRow(144,out var saucer) && saucer.TerritoryIntendedUse.RowId==23)
            cityNames[saucer.PlaceNameZone.RowId]=saucer.PlaceName.Value.Name.ExtractText();
        Add("Inn","inn",labels["inn"]);
        foreach(var key in new[]{"housing","district","inside","properties","mine","personal","apartment","fc-properties","fc","chambers"})Add("Housing",key,labels[key]);
        foreach(var t in districts)Add("Housing","territory:"+t.RowId,t.PlaceName.Value.Name.ExtractText());
        // One selectable zone per named territory, independent of instance number.
        var zones=sheet.Where(t=>t.IsInUse && (t.TerritoryIntendedUse.RowId==1 || WardrobeLocationPolicy.FieldOperation(t.TerritoryIntendedUse.RowId)) && t.PlaceName.RowId!=0)
            .GroupBy(t=>t.PlaceName.RowId).ToDictionary(g=>g.Key,g=>g.Min(t=>t.RowId));
        foreach(var t in sheet)
        {
            var use=t.TerritoryIntendedUse.RowId;
            var key="territory:"+t.RowId;
            var chain=new List<string>();
            bool housing=use is 13 or 14;
            var district=districts.Where(d=>d.PlaceNameRegion.RowId==t.PlaceNameRegion.RowId).ToArray();
            string? canonical=null;
            // Classify housing before checking labels: remodelled halls have unnamed rows.
            chain.AddRange(WardrobeLocationPolicy.HousingChain(use,district.Length==1?"territory:"+district[0].RowId:null));
            if(t.IsInUse && t.PlaceName.RowId!=0)
            {
                if(use==13)canonical=key;
                if(use==2)
                {
                    chain.Add("inn");
                    // An old single-inn rule must not silently become an all-inn rule.
                }
                if(!housing && (use is 0 or 2 or 23) && cityNames.TryGetValue(t.PlaceNameZone.RowId,out var city))
                {
                    var hub="city:"+t.PlaceNameZone.RowId;
                    Add("Hub",hub,city);chain.Add(hub);chain.Add("cities");
                    aliases[hub]=hub;
                    if(use!=2)canonical=hub;
                }
                if(zones.TryGetValue(t.PlaceName.RowId,out var zone) && (use==1 || WardrobeLocationPolicy.FieldOperation(use)))
                {
                    canonical="territory:"+zone;
                    Add("Zone",canonical,t.PlaceName.Value.Name.ExtractText());chain.Add(canonical);
                }
                if((housing || use is 0 or 1 or 2 or 23 || WardrobeLocationPolicy.FieldOperation(use)) && t.PlaceNameRegion.RowId is not (0 or 2405))
                {
                    var region="region:"+t.PlaceNameRegion.RowId;
                    Add("Region",region,t.PlaceNameRegion.Value.Name.ExtractText());chain.Add(region);aliases[region]=region;
                }
            }
            if(canonical!=null)aliases[key]=canonical;
            chain.Add("any");chains[key]=chain.Distinct().ToArray();
        }
        foreach(var key in new[]{"any","inn","cities","housing","district","inside","properties","mine","personal","apartment","fc-properties","fc","chambers"})aliases[key]=key;
    }
    internal string? Canonical(string key){Ensure();return aliases.GetValueOrDefault(key);}
    private string[] Parents(string key)=>chains.GetValueOrDefault(key)??[key,"any"];
    internal IEnumerable<string[]> PreviewPlaces(string key)
    {
        Ensure();
        if(chains.TryGetValue(key,out var exact))return [exact.SkipWhile(p=>p!=key).ToArray()];

        var ancestors=chains.Values.Where(c=>c.Contains(key)).Select(c=>c.SkipWhile(p=>p!=key).ToArray()).DistinctBy(c=>string.Join("|",c)).ToArray();
        if(ancestors.Length>0)return ancestors;
        if(WardrobeLocationPolicy.PropertyChain(key) is {Length:>0} properties)return chains.Values.Where(c=>c.Contains("inside")).Select(c=>properties.Concat(c).Distinct().ToArray());
        return [key=="any"?["any"]:[key,"any"]];
    }
    internal IEnumerable<string[]> PreviewJobs(string key)
    {
        Ensure();
        return key.StartsWith("job:") && uint.TryParse(key.AsSpan(4),out var id)?[JobChain(id)]:jobs.Values.Where(chain=>chain.Contains(key));
    }
    internal string Diagnostic {get;private set;}="";
    internal unsafe string[] Current(uint territory,uint map)
    {
        Ensure();
        var parent=Parents("territory:"+territory);
        var result=new List<string>();
        if(parent.Contains("inside") && HousingManager.Instance() is var housing && housing!=null && housing->IsInside())
        {
            var home=housing->GetCurrentIndoorHouseId();
            foreach(var (type,key) in new[]{(EstateType.PersonalEstate,"personal"),(EstateType.FreeCompanyEstate,"fc"),(EstateType.ApartmentRoom,"apartment"),(EstateType.PersonalChambers,"chambers")})
            {
                var owned=HousingManager.GetOwnedHouseId(type);
                if(home.Id!=0 && home.Id!=ulong.MaxValue && home.Id==owned.Id)
                {result.AddRange(WardrobeLocationPolicy.PropertyChain(key));break;}
            }
            if(home.Id!=0 && home.Id!=ulong.MaxValue && chains.TryGetValue("territory:"+home.TerritoryTypeId,out var district) && district.Contains("district"))
                parent=new[]{"inside"}.Concat(district.Where(p=>p!="district")).Distinct().ToArray();
        }
        var chain=result.Concat(parent).Distinct().ToArray();
        Diagnostic=$"territory {territory}; location chain {string.Join(" > ",chain)}";
        return chain;
    }
}
