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
