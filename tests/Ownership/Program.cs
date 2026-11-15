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
    var plan=new JObject { ["Equipment"]=planned,["Customize"]=hair,["BypassOwnership"]=bypass };
    var payload=WardrobeApplication.BuildPlan(playerGear,playerLook,plan,
        ["MainHand","Body","Ears","Feet"],["Hairstyle"],weapon,id=>id==400,skipped,out var applyLook);
    var gear=payload["Equipment"]!;
    if((gear["Body"]!=null)!=bypass || gear["MainHand"]!.Value<uint>("ItemId")!=(bypass?50u:41u)
        || gear["MainHand"]!.Value<bool>("Apply")!=bypass || gear["Ears"]==null || gear["Feet"]!=null
        || (bypass && gear["Body"]!.Value<byte>("Stain2")!=7) || skipped.Count!=(bypass?0:2)
        || !applyLook || payload["Customize"]!["Race"]!.Value<bool>("Apply")
        || payload["Customize"]!["Hairstyle"]!.Value<byte>("Value")!=22 || !JToken.DeepEquals(planned,original))
        throw new Exception("Ownership bypass changed the wrong slots, appearance or saved plan.");
}
Console.WriteLine("Wardrobe application checks passed.");

var zoneDesign=Guid.NewGuid();
var jobDesign=Guid.NewGuid();
var otherJob=Guid.NewGuid();
var bindings=new JObject{["Zones"]=new JObject{["200"]=zoneDesign.ToString()},["Jobs"]=new JObject{["10"]=jobDesign.ToString(),["11"]=otherJob.ToString()}};
var plans=new Dictionary<Guid,JObject>();
foreach(var designId in new[]{zoneDesign,jobDesign,otherJob})
    plans[designId]=new JObject{["Equipment"]=planned.DeepClone(),["Customize"]=hair.DeepClone(),["BypassOwnership"]=designId==zoneDesign};
foreach(bool zoneBypass in new[]{true,false})
{
    plans[zoneDesign]["BypassOwnership"]=zoneBypass;
    plans[jobDesign]["BypassOwnership"]=!zoneBypass;
    plans[otherJob]["BypassOwnership"]=!zoneBypass;
    foreach(var (job,zone,expected) in new[]{(10u,100u,jobDesign),(10u,200u,zoneDesign),(11u,200u,zoneDesign),(11u,100u,otherJob)})
    {
        var target=WardrobeAssignment.Resolve(bindings,123,job,zone,plans.ContainsKey);
        if(target?.Design!=expected)throw new Exception("Incorrect zone/job priority or job fallback.");
        var skipped=new List<string>();
        var payload=WardrobeApplication.BuildPlan(playerGear,playerLook,plans[target.Value.Design],
            ["Body","Ears","Feet"],["Hairstyle"],null,id=>id==400,skipped,out _);
        if((payload["Equipment"]!["Body"]!=null)!=plans[expected].Value<bool>("BypassOwnership") || payload["Equipment"]!["Feet"]!=null)
            throw new Exception("Assignment applied the wrong design's ownership policy.");
    }
}
if(WardrobeAssignment.Resolve(bindings,123,12,100,plans.ContainsKey)!=null
    || WardrobeAssignment.Resolve(bindings,123,10,200,id=>id!=zoneDesign)?.Design!=jobDesign)
    throw new Exception("Unassigned or deleted design fallback failed.");
Console.WriteLine("Job and zone assignment checks passed.");
