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
    private readonly Dictionary<string,string> labels=new(){["any"]="Anywhere",["inn"]="Any inn",["district"]="Any housing district",["inside"]="Any residential interior",["personal"]="My house",["fc"]="My FC house",["apartment"]="My apartment",["chambers"]="My FC room"};
    private readonly Dictionary<string,HashSet<string>> lists=[];
    private readonly Dictionary<string,string[]> chains=[];
    private readonly Dictionary<uint,string[]> jobs=[];
    internal int Revision {get;private set;}
    private readonly Dictionary<string,(string Key,string Name)[]> optionCache=[];
    private readonly Dictionary<uint,string> cityNames=[];
    private static readonly string[] RoleNames=["Tanks","Melee DPS","Ranged DPS","Healers","Caster DPS","Crafters","Gatherers"];

    internal string[] JobChain(uint id){Ensure();return jobs.GetValueOrDefault(id)??["job:"+id,"any"];}
    internal string Name(string key)
    {
        Ensure();
        if(labels.TryGetValue(key,out var name))return name;
        var parts=key.Split(':');
        if(parts is ["area",_,var raw] && uint.TryParse(raw,out var id) && names.TryGetRow(id,out var row))return row.Name.ExtractText();
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
            .OrderBy(x=>x.Key=="any"?0:1).ThenBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
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
        foreach(var t in sheet.Where(t=>t.TerritoryIntendedUse.RowId==0 && t.PlaceNameZone.RowId>0))
            cityNames.TryAdd(t.PlaceNameZone.RowId,t.PlaceNameZone.Value.Name.ExtractText());
        // The public Saucer and Chocobo Square share a zone ID; its localized map caption is the city label.
        if(sheet.TryGetRow(144,out var saucer) && saucer.TerritoryIntendedUse.RowId==23)
            cityNames[saucer.PlaceNameZone.RowId]=saucer.PlaceName.Value.Name.ExtractText();
        Add("Inn","inn","Any inn");
        foreach(var key in new[]{"district","inside","personal","fc","apartment","chambers"})Add("Housing",key,labels[key]);
        var districts=sheet.Where(t=>t.TerritoryIntendedUse.RowId==13 && t.PlaceNameRegion.RowId>0)
            .ToLookup(t=>t.PlaceNameRegion.RowId,t=>"territory:"+t.RowId);
        foreach(var t in sheet)
        {
            var use=t.TerritoryIntendedUse.RowId;
            var key="territory:"+t.RowId;
            var chain=new List<string>{key};
            bool ordinary=t.IsInUse && use is (0 or 1 or 2 or 13 or 14 or 23);
            if(use==14)
            {
                chain.Add("inside");
                var candidates=districts[t.PlaceNameRegion.RowId].Distinct().Take(2).ToArray();
                if(candidates.Length==1){chain.Add(candidates[0]);chain.Add("district");}
            }
            if(ordinary && t.PlaceName.RowId!=0)
            {
                string title=t.PlaceName.Value.Name.ExtractText();
                Add(use==2?"Inn":use is 13 or 14?"Housing":"City or zone",key,title);
                if(use==2)chain.Add("inn");
                if(use==13)chain.Add("district");
                if(cityNames.TryGetValue(t.PlaceNameZone.RowId,out var city))
                {
                    var cityKey="city:"+t.PlaceNameZone.RowId;
                    Add("City or zone",cityKey,city);
                    chain.Add(cityKey);
                }
                if(t.PlaceNameRegion.RowId!=0)
                {
                    var region="region:"+t.PlaceNameRegion.RowId;
                    Add("Region",region,t.PlaceNameRegion.Value.Name.ExtractText());
                    chain.Add(region);
                }
            }
            chain.Add("any");chains[key]=chain.ToArray();
        }
        foreach(var map in maps.Where(m=>m.PlaceNameSub.RowId!=0))
        {
            if(!sheet.TryGetRow(map.TerritoryType.RowId,out var t) || t.TerritoryIntendedUse.RowId is not (0 or 1 or 13 or 23))continue;
            if(map.PlaceNameSub.RowId==map.PlaceName.RowId)continue;
            var key="map:"+map.RowId;
            if(t.PlaceName.RowId==map.PlaceNameSub.RowId)
            {
                Add("Named area","territory:"+t.RowId,map.PlaceNameSub.Value.Name.ExtractText());
                continue;
            }
            Add("Named area",key,map.PlaceNameSub.Value.Name.ExtractText());
            chains[key]=[key,..Parents("territory:"+t.RowId)];
        }
    }
    private string[] Parents(string key)=>chains.GetValueOrDefault(key)??[key,"any"];
    internal IEnumerable<string[]> PreviewPlaces(string key)
    {
        Ensure();
        if(chains.TryGetValue(key,out var exact))return [exact];
        if(key.StartsWith("area:"))return [[key,..Parents("territory:"+key.Split(':')[1])]];
        var ancestors=chains.Values.Where(c=>c.Contains(key)).Select(c=>c.SkipWhile(p=>p!=key).ToArray()).DistinctBy(c=>string.Join("|",c)).ToArray();
        if(ancestors.Length>0)return ancestors;
        if(key is "personal" or "fc" or "apartment" or "chambers")return chains.Values.Where(c=>c.Contains("inside")).Select(c=>new[]{key}.Concat(c).ToArray());
        return [key=="any"?["any"]:[key,"any"]];
    }
    internal IEnumerable<string[]> PreviewJobs(string key)
    {
        Ensure();
        return key.StartsWith("job:") && uint.TryParse(key.AsSpan(4),out var id)?[JobChain(id)]:jobs.Values.Where(chain=>chain.Contains(key));
    }
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
                if(home.Id!=0 && home.Id!=ulong.MaxValue && home.Id==owned.Id)result.Insert(0,key);
            }
            if(home.Id!=0 && home.Id!=ulong.MaxValue && chains.TryGetValue("territory:"+home.TerritoryTypeId,out var district) && district.Contains("district"))
                parent=new[]{parent[0],"inside"}.Concat(district).Distinct().ToArray();
        }
        if(chains.TryGetValue("map:"+map,out var area) && area.Contains("territory:"+territory))result.Add("map:"+map);
        var baseChain=result.Concat(parent).Distinct().ToArray();
        if(parent.Any(p=>p.StartsWith("region:")) && TerritoryInfo.Instance() is var info && info!=null)
        {
            foreach(uint place in new uint[]{info->AreaPlaceNameId,info->SubAreaPlaceNameId})
            {
                if(place==0 || !names.TryGetRow(place,out var row))continue;
                var key=$"area:{territory}:{place}";
                if(result.Contains(key))continue;
                if(territories.TryGetRow(territory,out var t) && (place==t.PlaceName.RowId || place==t.PlaceNameZone.RowId || place==t.PlaceNameRegion.RowId))continue;
                Add("Named area",key,row.Name.ExtractText());
                baseChain=[key,..baseChain];chains[key]=baseChain;result.Insert(0,key);
            }
        }
        return baseChain;
    }
}
