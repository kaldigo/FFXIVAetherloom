using Aetherloom;

void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
var json = """
{"Plates":[{"NodeType":"Folder","Name":"Tank/Melee","Children":[
  {"NodeType":"Folder","Name":"Empty","Children":[]},
  {"NodeType":"Plate","Id":"fixed","Plate":{"Name":"Armour","Tags":["Raid"],"FillWithNewEmperor":true,
  "Items":{"LeftRing":{"ItemId":44,"Stain1":9,"Stain2":17}}}}]},
  {"Name":"Legacy","Items":{"Head":{"ItemId":42,"StainId":6}}}]}
""";
var lib=WardrobeImport.Glamaholic(json);
var first=lib.Plates[0];
Check(lib.Folders.SequenceEqual(["/Tank／Melee","/Tank／Melee/Empty"]), "folder hierarchy");
Check(first.Folder==lib.Folders[0] && first.Tags.SequenceEqual(["Raid"]), "plate placement and tags");
Check(first.Items["LFinger"]==new ImportedItem(44,9,17) && first.Items["RFinger"].Id==9295 && !first.Items.ContainsKey("MainHand"),"sided rings, dyes and Emperor fill");
Check(lib.Plates[1].Items.Count==1 && lib.Plates[1].Items["Head"].First==6, "legacy sparse plate");
Check(WardrobeImport.Glamaholic(json.Replace("Armour","Renamed")).Plates[0].Id==first.Id,"stable source identity");
Check(WardrobeImport.CollectionId("https://ffxiv.eorzeacollection.com/glamour/123/example?foo=1")==123,"public URL");
var ec=WardrobeImport.Collection(123,"""{"id":123,"name":"Set","gear":{"head":{"name":"My Hat","dyes":"none,opo-opo-brown"},"weapon":null,"body":{"name":"Missing"},"facewear":{"name":"Ignored"}}}""",
    (slot,name)=>slot=="Head" && name=="My Hat"?3u:null, dye=>dye=="opo opo brown"?(byte)7:null);
Check(ec.Plates.Single().Items.Single().Value==new ImportedItem(3,0,7) && ec.Warnings.Count==1,"collection slots, issue reporting and second dye");
Console.WriteLine("Wardrobe import checks passed.");

var metadata=new[]{"STYLE:Casual","Blue","Travel","gender:Female","intended:Bard"}.Select(WardrobeMetadata.Decode).ToArray();
Check(metadata[0].Group=="Style" && metadata[1].Group=="Color" && metadata[2].Group=="Tags","prefixed and legacy tags");
foreach(var value in new[]{"Casual","style:Custom","tag:Travel","My tag"})
{
    var decoded=WardrobeMetadata.Decode(WardrobeMetadata.Encode("Tags",value));
    Check(decoded.Group=="Tags" && decoded.Value==value,"freeform tag round trip");
}
Check(WardrobeMetadata.Single(WardrobeMetadata.Gender,[])=="Unspecified" &&
    WardrobeMetadata.Single(WardrobeMetadata.Intended,[])=="All Classes","unclassified design defaults");
Check(WardrobeMetadata.ContainsText("Bard","brd") && WardrobeMetadata.ContainsText("Beastmaster","bst"),"job abbreviation lookup");
var filters=WardrobeMetadata.Filters.ToDictionary(g=>g,_=>new HashSet<string>(StringComparer.OrdinalIgnoreCase));
filters["Color"].UnionWith(["Red","blue"]);
filters["Style"].Add("Casual");
filters[WardrobeMetadata.Weapon].Add("Dancer");
Check(WardrobeMetadata.Matches(metadata,filters,()=>["Gunbreaker","Dancer"]),"any value per category and alternate main hand");
Check(!WardrobeMetadata.Matches(metadata,filters,()=>["Bard"]),"weapon filter uses configured weapons");
filters["Style"].Clear();filters["Style"].Add("Vintage");
Check(!WardrobeMetadata.Matches(metadata,filters,()=>["Dancer"]),"all active categories must match");
Console.WriteLine("Wardrobe metadata checks passed.");

var jobRows=new (string,string,string?,string?)[]{("BRD","bard","barde","BRD"),("brd","Bard",null,"BAR"),("","unused",null,null),("","unused",null,null)};
var aliases=jobRows.ToJobAliases();
Check(aliases.Count==1 && aliases["brd"].SequenceEqual(["bard","barde","BRD","BAR"]),"weapon job search merges duplicate abbreviations and skips unnamed rows");
