using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class MigrationChecks
{
    private static readonly string[] Slots=["MainHand","OffHand","Head","Body","Hands","Legs","Feet","Ears","Neck","Wrists","RFinger","LFinger"];

    internal static void Run()
    {
        Guid stored=Guid.NewGuid(),external=Guid.NewGuid(),baseId=Guid.NewGuid();
        var plan=JObject.Parse("""{"Equipment":{"Head":{"ItemId":100,"Apply":false}},"AdditionalMainHands":{"MainHand/1":{"ItemId":200}},"Customize":{},"Tags":["old"],"LibraryLinked":true}""");
        plan["BaseDesign"]=baseId.ToString();
        var original=new JObject{["Plans"]=new JObject{[stored.ToString()]=plan},["FavouriteDesigns"]=new JArray(stored.ToString()),
            ["AssignmentMode"]=true,["Assignments"]=new JObject{["123"]=new JObject{["Rules"]=new JObject{["any|job:21"]=stored.ToString()}}}};
        var entries=new LibraryDesign[]{new(stored,"Set",["aetherloom","theme:Royalty"],"[AL] A/B"),
            new(external,"Set",["aetherloom"],"Other"),new(baseId,"Race",["aetherloom-base"],"Bases")};
        var externalPlan=JObject.Parse("""{"Customize":{},"Equipment":{"Feet":{"ItemId":300},"Body":{"ItemId":400}}}""");
        var untouched=(JObject)original.DeepClone();
        try
        {
            WardrobeStore.Migrate(original,entries,[],_=>null);
            throw new Exception("Accepted an incomplete migration.");
        }
        catch(InvalidDataException) {}
        var result=WardrobeStore.Migrate(original,entries,["[AL] A","[AL] A/B","[AL] A/Empty","Bases"],
            id=>id==external?externalPlan:throw new Exception("Fetched an existing plan or a base as a wardrobe outfit."));
        var plans=(JObject)result["Plans"]!;
        Check(JToken.DeepEquals(original,untouched) && externalPlan["Folder"]==null,"source designs mutated");
        Check(plans.Count==2 && plans[baseId.ToString()]==null && (string?)plans[stored.ToString()]!["BaseDesign"]==baseId.ToString(),"linked base identity");
        Check((string?)plans[external.ToString()]!["Folder"]=="" && (string?)plans[stored.ToString()]!["Folder"]=="[AL] A/B","folder placement");
        Check(plans[stored.ToString()]!["Tags"]!.Values<string>().Contains("theme:Royalty"),"authoritative tags");
        foreach(var key in new[]{"FavouriteDesigns","Assignments","AssignmentMode"})
            Check(JToken.DeepEquals(original[key],result[key]),key);
        Check(WardrobeStore.ItemIds(plans,Slots,id=>id!=400).SequenceEqual(new uint[]{100,200,300}),"Steward items");
        WardrobeStore.ChangeFolder(result,"[AL] A","","New");
        WardrobeStore.ChangeFolder(result,"[AL] New","",null);
        Check((string?)plans[stored.ToString()]!["Folder"]=="[AL] B" && result["Folders"]!.Values<string>().Contains("[AL] Empty"),"folder promotion");
        Console.WriteLine("Wardrobe library migration checks passed.");
    }

    internal static void Rehearse(string configRoot)
    {
        var original=JObject.Parse(File.ReadAllText(Path.Combine(configRoot,"Aetherloom","wardrobe-plans.json")));
        var folder=Path.Combine(configRoot,"Glamourer");
        var sources=Directory.GetFiles(Path.Combine(folder,"designs"),"*.json").Select(f=>JObject.Parse(File.ReadAllText(f)))
            .ToDictionary(d=>Guid.Parse(d.Value<string>("Identifier")!));
        var entries=sources.Select(p=>new LibraryDesign(p.Key,p.Value.Value<string>("Name")!,p.Value["Tags"]?.ToObject<string[]>()??[],
            p.Value.Value<string>("FileSystemFolder")??"")).ToArray();
        var folders=((JObject)JObject.Parse(File.ReadAllText(Path.Combine(folder,"design_filesystem","organization.json")))["Folders"]!).Properties().Select(p=>p.Name).ToArray();
        var result=WardrobeStore.Migrate(original,entries,folders,id=>sources[id]);
        var plans=(JObject)result["Plans"]!;
        var expected=new HashSet<uint>();
        var tagged=entries.Where(e=>WardrobeLibrary.Tagged(e.Tags,WardrobeLibrary.Tag)).ToArray();
        Check(plans.Count==tagged.Length,"design count");
        foreach(var e in tagged)
        {
            var id=e.Id.ToString();
            var old=original["Plans"]?[id]??sources[e.Id];
            var migrated=plans[id]!;
            foreach(var field in new[]{"Equipment","AdditionalMainHands","Customize","BaseDesign","LipColorOverride","BypassOwnership"})
                Check(JToken.DeepEquals(old[field],migrated[field]),field);
            Check((string?)migrated["Folder"]==(WardrobeLibrary.Managed(e.Folder)?e.Folder:"") &&
                ((JArray)migrated["Tags"]!).Values<string>().ToHashSet().SetEquals(e.Tags),"placement or tags");
            var equipment=(JObject)old["Equipment"]!;
            foreach(var item in Slots.Select(s=>equipment[s]).Concat((old["AdditionalMainHands"] as JObject)?.Properties().Select(p=>p.Value)??[]))
                if(item?.Value<ulong?>("ItemId") is >0 and <=uint.MaxValue and var n)expected.Add((uint)n);
        }
        Check(expected.SetEquals(WardrobeStore.ItemIds(plans,Slots,_=>true)),"Steward candidate item IDs");
        foreach(var key in new[]{"Assignments","AssignmentMode","FavouriteDesigns"})
            Check(JToken.DeepEquals(original[key],result[key]),key);
        Check(result["Folders"]!.Values<string>().ToHashSet().SetEquals(folders.Where(WardrobeLibrary.Managed)),"folder list");
        Console.WriteLine($"Read-only rehearsal passed for {plans.Count} designs, {result["Folders"]!.Count()} folders and {expected.Count} distinct item references before game-sheet validation.");
    }
    private static void Check(bool condition,string detail)
    {
        if(!condition)throw new Exception("Wardrobe migration check failed: "+detail);
    }
}
