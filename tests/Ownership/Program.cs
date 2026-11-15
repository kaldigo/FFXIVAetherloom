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

var wearablePlan=JObject.Parse("""{"Equipment":{"Body":{"ItemId":1,"Apply":true},"Ears":{"ItemId":2,"Apply":false,"ApplyStain":true}},"AdditionalMainHands":{"MainHand/2":{"ItemId":3,"Apply":true}},"Tags":["gender:Male"],"BypassOwnership":true}""");
int ItemMask(ulong id)=>id switch {1=>WardrobeWearability.Everyone,2=>0x101,3=>0x280,_=>0};
int Mask()=>WardrobeWearability.DesignMask(wearablePlan,["Body","Ears"],ItemMask);
var gearMask=Mask();
if(!WardrobeWearability.Allows(gearMask,8,1) || WardrobeWearability.Allows(gearMask,1,1) || WardrobeWearability.Allows(gearMask,8,0))
    throw new Exception("Re-evaluate the same equipment against the visible race and gender.");
wearablePlan["Equipment"]!["Ears"]!["Apply"]=true;
if(WardrobeWearability.Allows(Mask(),8,1))throw new Exception("An incompatible accessory must hide the design.");
wearablePlan.Remove("AdditionalMainHands");
if(!WardrobeWearability.Allows(Mask(),1,0))throw new Exception("Compatible restricted gear must remain available.");
Console.WriteLine("Wardrobe wearability checks passed.");

var chainRules=new JObject();
string[] coast=["region:22","any"], limsa=["city:500",..coast], inn=["territory:177","inn",..limsa];
string[] warrior=["job:21","role:0","any"], paladin=["job:19","role:0","any"];
Guid regional=Guid.NewGuid(), cityTank=Guid.NewGuid(),cityWar=Guid.NewGuid(),innOwner=Guid.NewGuid();
void Demand(bool value,string why){if(!value)throw new Exception(why);}
bool Visible(Guid _)=>true;
Demand(WardrobeAssignment.Edit(chainRules,"region:22|job:21",regional,null,false),"Create region rule");
Demand(WardrobeAssignment.Winner(chainRules,inn,warrior,Visible)?.Design==regional,"Inn inherits region without entry history");
Demand(WardrobeAssignment.Previous(chainRules,limsa,warrior,"job:21",Visible)?.Design==regional,"Editor reports the next regional Warrior rule");
WardrobeAssignment.Edit(chainRules,"city:500|role:0",cityTank,null,false);
WardrobeAssignment.Edit(chainRules,"city:500|job:21",cityWar,null,false);
Demand(WardrobeAssignment.Winner(chainRules,inn,warrior,Visible)?.Design==cityWar && WardrobeAssignment.Winner(chainRules,inn,paladin,Visible)?.Design==cityTank,"City/job specificity inside inn");
Demand(WardrobeAssignment.Previous(chainRules,inn,warrior,"any",Visible)?.Design==cityWar,"Inn generic overrides city specific");
WardrobeAssignment.Edit(chainRules,"inn|any",innOwner,null,false);
Demand(WardrobeAssignment.Winner(chainRules,inn,warrior,Visible)?.Design==innOwner,"Inn rule wins");
Demand(!WardrobeAssignment.Edit(chainRules,"inn|any",cityTank,null,false) && !WardrobeAssignment.Edit(chainRules,"inn|any",cityTank,cityWar,false),"Duplicate or stale move rejected");
Demand(WardrobeAssignment.Edit(chainRules,"inn|any",cityTank,innOwner,false) && chainRules.Count==4,"Confirmed move replaces one owner");
var aliases=JObject.Parse("""{"Jobs":{"021":"invalid","21":"00000000-0000-0000-0000-000000000001","0021":"00000000-0000-0000-0000-000000000002","bad":{}}}""");
Demand(WardrobeAssignment.Rules(aliases).Count==1 && WardrobeAssignment.Rules(aliases).Count==1,"Malformed and duplicate legacy conditions migrate idempotently");
var activeFields=JObject.Parse("""{"Body":3,"Head":3,"Hairstyle":{"Before":2,"Applied":5},"FacePaint":{"Before":4,"Applied":8}}""");
var visibleLook=JObject.Parse("""{"Hairstyle":{"Value":5},"FacePaint":{"Value":9},"Race":{"Value":8}}""");
var transition=JObject.Parse("""{"Equipment":{"Body":{"Apply":true,"ApplyStain":true,"ItemId":30}},"Customize":{}}""");
JObject RealGear(int index)=>new(){["ItemId"]=100+index,["Stain"]=2,["Stain2"]=4};
Demand(WardrobeApplication.PlanRestoration(activeFields,transition,new(),visibleLook,false,RealGear,["Hairstyle","FacePaint"],out var remaining,out var lookChanged),"Restore on fallback");
Demand(lookChanged && (int?)transition["Customize"]?["Hairstyle"]?["Value"]==2 && transition["Customize"]?["FacePaint"]==null && transition["Customize"]?["Race"]==null,"Restore unchanged managed hair, preserve external face paint and race");
Demand((int?)transition["Equipment"]?["Head"]?["ItemId"]==102 && (int?)transition["Equipment"]?["Head"]?["Stain2"]==4 && (int?)remaining["Body"]==3 && remaining.Count==1,"Released slot reads actual equipped gear and dyes");
Console.WriteLine("Assignment chain, conflicts, migration and restoration checks passed.");

var origin=JObject.Parse("""{"Clan":{"Value":1},"Race":{"Value":1},"Hairstyle":{"Value":5},"Face":{"Value":3}}""");
var baseA=JObject.Parse("""{"Clan":{"Value":15,"Apply":true},"Hairstyle":{"Value":3,"Apply":true},"Height":{"Value":99,"Apply":false}}""");
var outfit=JObject.Parse("""{"Hairstyle":{"Value":7,"Apply":true},"Face":{"Value":9,"Apply":true}}""");
var merged=WardrobeBase.Compose(baseA,outfit);
Demand((int?)merged["Race"]?["Value"]==8 && (int?)merged["Hairstyle"]?["Value"]==7 && merged["Height"]==null && merged["Face"]==null,"Base composition respects enabled fields, clan and outfit precedence");
var based=new JObject{["Equipment"]=new JObject(),["Customize"]=merged};
var trackedFields=WardrobeBase.Appearance.Select(f=>f.Key).ToArray();
Demand(WardrobeApplication.PlanRestoration(new(),based,new(),origin,true,RealGear,trackedFields,out var tracked,out _,baseA),"Manual base applied");
var actual=(JObject)merged.DeepClone();actual["Face"]=new JObject{["Value"]=2};
WardrobeBase.RecordApplied(new(),tracked,origin,actual,baseA);
foreach(bool manual in new[]{true,false})
{
    var clean=new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject()};
    Demand(WardrobeApplication.PlanRestoration(tracked,clean,new(),actual,manual,RealGear,trackedFields,out var released,out var restore),"Switch away from linked base");
    Demand(restore && released.Count==0 && (int?)clean["Customize"]?["Race"]?["Value"]==1 && (int?)clean["Customize"]?["Face"]?["Value"]==3,"Restore explicit and normalized fields in either mode");
}
var baseB=JObject.Parse("""{"Clan":{"Value":5,"Apply":true}}""");
var second=new JObject{["Equipment"]=new JObject(),["Customize"]=WardrobeBase.Compose(baseB,new())};
Demand(WardrobeApplication.PlanRestoration(tracked,second,new(),actual,false,RealGear,trackedFields,out var secondTracked,out _,baseB),"Replace one linked base with another");
Demand((int?)secondTracked["Clan"]?["Before"]==1 && (int?)secondTracked["Clan"]?["Applied"]==5 && (int?)second["Customize"]?["Hairstyle"]?["Value"]==5,"Replacing a base keeps the original clan for later restoration and releases its hairstyle");
var edited=(JObject)actual.DeepClone();edited["Face"]!["Value"]=4;
var withoutBase=new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject()};
Demand(WardrobeApplication.PlanRestoration(tracked,withoutBase,new(),edited,true,RealGear,trackedFields,out var afterEdit,out _) && withoutBase["Customize"]!["Face"]==null && afterEdit["Face"]==null,"Preserve a later external edit while clearing the old link");
Console.WriteLine("Linked appearance layering and restoration checks passed.");
