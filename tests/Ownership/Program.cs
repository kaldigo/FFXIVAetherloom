using Newtonsoft.Json.Linq;
using System.Text.Json;
using Aetherloom;

const uint boots = 400;
void Check(OwnershipSnapshot snapshot, string expected, bool current = true, bool exempt = false)
{
    var actual = snapshot.Availability(boots, exempt, current);
    if (actual != expected) throw new Exception($"Expected {expected}, got {actual}.");
}

var empty = new OwnershipSnapshot([], [], [], [], []);
Check(empty, "Missing");
Check(empty with { Bags = [boots] }, "Inventory");
Check(empty with { Bags = [], Armoury = [boots] }, "Armoury Chest");
Check(empty with { Equipped = [boots] }, "Equipped");
Check(empty with { Dresser = [boots] }, "Glamour Dresser");
Check(empty with { Armoire = [boots] }, "Armoire");
Check(empty with { Armoury = null }, "Unconfirmed");
Check(empty with { Armoury = null, Bags = [boots] }, "Inventory");
Check(empty with { Bags = [boots] }, "Unconfirmed", current: false);
Check(empty, "Event / Mog Station exemption", exempt: true);
var stored = JsonSerializer.Serialize(empty with { Armoury = [boots] });
Check(JsonSerializer.Deserialize<OwnershipSnapshot>(stored)!, "Armoury Chest");
var legacy = JsonSerializer.Deserialize<OwnershipSnapshot>("""{"Dresser":[400],"Armoire":[]}""")!;
Check(legacy, "Glamour Dresser");
Check(legacy with { Dresser = [] }, "Unconfirmed");
Console.WriteLine("Ownership checks passed.");

var planned=JObject.Parse("""{"Body":{"ItemId":50,"Stain":3,"Stain2":7,"Apply":true,"ApplyStain":true},"Ears":{"ItemId":400,"Apply":true},"Feet":{"ItemId":50,"Apply":false},"Hat":{"Show":false,"Apply":true}}""");
var original=planned.DeepClone();
var playerLook=JObject.Parse("""{"Race":{"Value":4,"Apply":true},"Hairstyle":{"Value":2,"Apply":true}}""");
var hair=JObject.Parse("""{"Hairstyle":{"Value":22,"Apply":true}}""");
var weapon=JObject.Parse("""{"ItemId":50,"Apply":true}""");
var playerGear=JObject.Parse("""{"MainHand":{"ItemId":41,"Apply":true}}""");
foreach(bool bypass in new[]{false,true,false})
{
    var skipped=new List<string>();
    var payload=WardrobeApplication.Build(playerGear,playerLook,planned,hair,
        ["MainHand","Body","Ears","Feet"],["Hairstyle"],weapon,id=>bypass || id==400,skipped,out var applyLook);
    var gear=payload["Equipment"]!;
    if((gear["Body"]!=null)!=bypass || gear["MainHand"]!.Value<uint>("ItemId")!=(bypass?50u:41u)
        || gear["MainHand"]!.Value<bool>("Apply")!=bypass || gear["Ears"]==null || gear["Feet"]!=null
        || (bypass && gear["Body"]!.Value<byte>("Stain2")!=7) || skipped.Count!=(bypass?0:2)
        || !applyLook || payload["Customize"]!["Race"]!.Value<bool>("Apply")
        || payload["Customize"]!["Hairstyle"]!.Value<byte>("Value")!=22 || !JToken.DeepEquals(planned,original))
        throw new Exception("Ownership bypass changed the wrong slots, appearance or saved plan.");
}
Console.WriteLine("Wardrobe application checks passed.");
