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
// A cold login loads yesterday's armoire and refreshes carried containers before the cabinet opens.
var persistedArmoire=JsonSerializer.Serialize(empty with { Armoire=[boots] });
var login=JsonSerializer.Deserialize<OwnershipSnapshot>(persistedArmoire)! with { Bags=[99],Armoury=[],Equipped=[] };
Check(JsonSerializer.Deserialize<OwnershipSnapshot>(JsonSerializer.Serialize(login))!, "Armoire");
Check(login.RefreshArmoire(false,()=>throw new Exception("Read closed armoire at login")), "Armoire");
Check(login.RefreshArmoire(true,()=>null), "Armoire");
Check(login.RefreshArmoire(true,()=>[]), "Missing");
Check((login with { Armoire=null }).RefreshArmoire(false,()=>[]), "Unconfirmed");
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

foreach(var field in new[]{"Weapon","Hat","Visor","VieraEars"})
foreach(bool show in new[]{false,true})
foreach(bool manual in new[]{false,true})
{
    string attribute=field=="Visor"?"IsToggled":"Show";
    var currentGear=(JObject)playerGear.DeepClone();
    currentGear[field]=new JObject{[attribute]=!show};
    var design=new JObject{["Equipment"]=new JObject{[field]=new JObject{[attribute]=show,["Apply"]=true}},["Customize"]=new JObject()};
    var payload=WardrobeApplication.BuildPlan(currentGear,playerLook,design,["MainHand","OffHand"],["Hairstyle"],null,_=>false,[],out var applyLook);
    if(!WardrobeApplication.PlanRestoration(new JObject(),payload,currentGear,playerLook,manual,_=>null,["Hairstyle"],out var displayTracked,out _)
        || payload["Equipment"]![field]!.Value<bool>(attribute)!=show || !payload["Equipment"]![field]!.Value<bool>("Apply")
        || payload["Equipment"]!["MainHand"]!.Value<bool>("Apply") || applyLook
        || ((JObject)payload["Customize"]!).Properties().Any(p=>p.Value is JObject entry && entry.Value<bool>("Apply"))
        || displayTracked[field]!.Value<bool>("Applied")!=show)
        throw new Exception("Display-only design changed equipment or appearance, or lost its display setting.");
    var actualGear=(JObject)currentGear.DeepClone();
    actualGear[field]=payload["Equipment"]![field]!.DeepClone();
    var cleared=WardrobeApplication.BuildPlan(actualGear,playerLook,new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject()},["MainHand","OffHand"],[],null,_=>false,[],out _);
    if(!WardrobeApplication.PlanRestoration(displayTracked,cleared,actualGear,playerLook,false,_=>null,[],out _,out _)
        || cleared["Equipment"]![field]!.Value<bool>(attribute)!=!show)
        throw new Exception("Assignment fallback did not restore the display setting.");
}

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

{
    var plan=JObject.Parse("""{"Equipment":{"MainHand":{"ItemId":100},"OffHand":{"ItemId":300,"Stain2":12,"Apply":true}},"AdditionalMainHands":{"MainHand/paladin":{"ItemId":200}}}""");
    WardrobeWeapons.Upgrade(plan,(main,off)=>main==200 && off==300);
    var hands=WardrobeWeapons.Hands(plan).ToArray();
    var shield=WardrobeWeapons.OffHand(plan,hands[1].Key,hands[1].Slot,2,false)!;
    if(shield.Value<uint>("ItemId")!=300 || shield.Value<byte>("Stain2")!=12 || plan["Equipment"]!["OffHand"]!=null)
        throw new Exception("Legacy shield was not retained with its compatible alternate.");
    if(WardrobeWeapons.OffHand(plan,hands[0].Key,hands[0].Slot,3,false)!=null ||
        WardrobeWeapons.OffHand(plan,hands[0].Key,hands[0].Slot,84,true)!.Value<uint>("ItemId")!=100)
        throw new Exception("Independent and built-in off hands were confused.");
    if(!WardrobeStore.ItemIds(new JObject{["design"]=plan},new[]{"MainHand","OffHand"},_=>true).SequenceEqual(new uint[]{100,200,300}))
        throw new Exception("Steward lost a paired item.");
    ((JObject)plan["PairedOffHands"]!).Remove(hands[1].Key);
    var emptyShield=WardrobeWeapons.OffHand(plan,hands[1].Key,hands[1].Slot,2,false)!;
    if(!emptyShield.Value<bool>("Apply") || emptyShield.Value<uint>("ItemId")!=WardrobeWeapons.EmptyShield ||
        WardrobeWeapons.OffCategory(8)!=11 || WardrobeWeapons.OffCategory(9)!=0 || WardrobeWeapons.OffCategory(12)!=13)
        throw new Exception("Empty shield or off-hand eligibility failed.");
}
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
string[] coast=["region:22","any"], limsa=["city:500","cities",..coast], inn=["territory:177","inn",..limsa];
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
var allCities=Guid.NewGuid();
Demand(WardrobeAssignment.Edit(chainRules,"cities|any",allCities,null,false),"Create all-cities assignment");
Demand(!WardrobeAssignment.Edit(chainRules,"cities|any",regional,null,false),"Reject duplicate all-cities assignment");
Demand(WardrobeAssignment.Winner(chainRules,["territory:178","inn","city:504","cities","region:24","any"],warrior,Visible)?.Design==cityTank,"Inn override wins above all cities");
Demand(WardrobeAssignment.Winner(chainRules,["city:504","cities","region:24","any"],warrior,Visible)?.Design==allCities,"All cities matches a different city");
Demand(WardrobeAssignment.Winner(chainRules,limsa,warrior,Visible)?.Design==cityWar,"Specific city wins above all cities");
Demand(WardrobeAssignment.Previous(chainRules,limsa,warrior,"any",Visible)?.Design==allCities,"City editor shows all cities as its fallback");
Demand(WardrobeAssignment.Winner(chainRules,coast,warrior,Visible)?.Design==regional,"Countryside retains its regional assignment");
var batchRules=new JObject{["cities|job:21"]=cityWar.ToString()};
var batchOriginal=batchRules.DeepClone();
var requested=new Dictionary<string,Guid?>{["cities|job:19"]=null,["cities|job:21"]=null};
Demand(!WardrobeAssignment.EditMany(batchRules,regional,requested) && JToken.DeepEquals(batchRules,batchOriginal),"Conflicting batch must not partially save");
requested["cities|job:21"]=cityWar;
Demand(WardrobeAssignment.EditMany(batchRules,regional,requested) && batchRules.Count==2
    && batchRules.Properties().All(p=>p.Value.ToString()==regional.ToString()),"Confirmed batch adds and moves all assignments");
var retry=requested.Keys.ToDictionary(key=>key,_=>(Guid?)regional);
Demand(WardrobeAssignment.EditMany(batchRules,regional,retry) && batchRules.Count==2,"Repeated batch does not duplicate assignments");
var aliases=JObject.Parse("""{"Jobs":{"021":"invalid","21":"00000000-0000-0000-0000-000000000001","0021":"00000000-0000-0000-0000-000000000002","bad":{}}}""");
Demand(WardrobeAssignment.Rules(aliases).Count==1 && WardrobeAssignment.Rules(aliases).Count==1,"Malformed and duplicate legacy conditions migrate idempotently");
// Location simplification preserves owners and archives ambiguous/removed conditions.
var simplified=new JObject{["100"]=new JObject{["Rules"]=new JObject{
    ["city:500|any"]=cityWar.ToString(),["territory:128|any"]=cityWar.ToString(),
    ["territory:129|any"]=regional.ToString(),["area:128:5|any"]=regional.ToString(),
    ["territory:282|job:21"]=cityWar.ToString(),["personal|any"]=cityTank.ToString(),
    ["territory:135|any"]=regional.ToString()}}};
string? CanonicalPlace(string place)=>place switch {
    "territory:128" or "territory:129"=>"city:500",
    "area:128:5" or "territory:282"=>null,
    _=>place};
Demand(WardrobeLocationPolicy.Migrate(simplified,CanonicalPlace),"Location migration runs once");
var simplifiedSettings=(JObject)simplified["100"]!;
var simplifiedRules=(JObject)simplifiedSettings["Rules"]!;
Demand(simplifiedRules.Count==3 && WardrobeAssignment.Owner(simplifiedRules,"city:500|any")==cityWar,"Canonical rule wins; same-owner aliases deduplicate");
Demand(simplifiedSettings["ReviewRules"] is JObject {Count:3},"Conflicts, removed areas and house sizes remain available for review");
Demand(simplifiedSettings["ReviewRules"]?["territory:129|any"]?["Design"]?.ToString()==regional.ToString(),"Conflicting owner is retained");
var migratedSnapshot=simplified.DeepClone();
Demand(!WardrobeLocationPolicy.Migrate(simplified,CanonicalPlace) && JToken.DeepEquals(simplified,migratedSnapshot),"Migration is idempotent including review entries");
foreach(var property in new[]{"personal","apartment","fc","chambers"})
{
    var propertyChain=WardrobeLocationPolicy.PropertyChain(property);
    Demand(propertyChain[0]==property && propertyChain[^1]=="properties","Specific property precedes its umbrellas");
    Demand(propertyChain[1]==(property is "personal" or "apartment"?"mine":"fc-properties"),"Personal and FC properties remain separate");
}
Demand(WardrobeLocationPolicy.FieldOperation(41) && WardrobeLocationPolicy.FieldOperation(48) && WardrobeLocationPolicy.FieldOperation(61)
    && !WardrobeLocationPolicy.FieldOperation(3),"Eureka, Bozja and new field operations are included without dungeons");
var housingPriorities=new[]{"personal","mine","properties","inside","territory:339","housing","region:22","any"};
var priorityRules=new JObject();
for(int i=housingPriorities.Length-1;i>=0;i--)
{
    priorityRules[housingPriorities[i]+"|any"]=Guid.NewGuid().ToString();
    Demand(WardrobeAssignment.Winner(priorityRules,housingPriorities,warrior,Visible)?.Key==housingPriorities[i]+"|any","Each housing specificity level wins over its parents");
}
Console.WriteLine("Simplified location migration and housing priorities passed.");

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
WardrobeBase.RecordApplied(new(),tracked,origin,actual,baseA,merged);
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

var lips=new JObject{["Value"]=173,["Apply"]=true};
var baseMakeup=JObject.Parse("""{"LipColor":{"Value":8,"Apply":true},"Lipstick":{"Value":0,"Apply":true}}""");
var painted=WardrobeBase.WithLipColor(baseMakeup,lips,1,null);
Demand((int?)painted["LipColor"]?["Value"]==173 && (int?)painted["Lipstick"]?["Value"]==128,"Apply light lip shade over a linked appearance");
Demand(ReferenceEquals(WardrobeBase.WithLipColor(baseMakeup,null,1,null),baseMakeup),"An existing outfit inherits its base makeup");
var fur=WardrobeBase.Compose(JObject.Parse("""{"Clan":{"Value":13,"Apply":true},"LipColor":{"Value":3,"Apply":true}}"""),new());
Demand(ReferenceEquals(WardrobeBase.WithLipColor(fur,lips,1,null),fur),"A linked Hrothgar appearance keeps its fur pattern");
Console.WriteLine("Lip shade composition checks passed.");

var wornHair=JObject.Parse("""{"Hairstyle":{"Before":3,"Applied":11,"Base":false},"HairColor":{"Before":8,"Applied":4},"Highlights":{"Before":0,"Applied":128},"HighlightsColor":{"Before":22,"Applied":16}}""");
foreach(var (key,_,mask) in WardrobeBase.Appearance.Where(f=>wornHair.ContainsKey(f.Key)))
{
    byte shown=wornHair[key]!.Value<byte>("Applied");
    Demand(WardrobeBase.TryValue(key,shown,mask,new(),wornHair,out var neutral) && neutral==wornHair[key]!.Value<byte>("Before"),"Ignore the worn outfit's "+key+" in another preview");
}
var styled=JObject.Parse("""{"Hairstyle":{"Value":7,"Apply":true}}""");
foreach(var target in new[]{WardrobeBase.Compose(styled,new()),WardrobeBase.Compose(null,styled)})
    Demand(WardrobeBase.TryValue("Hairstyle",11,255,target,wornHair,out var shown) && shown==7,"The previewed base or outfit sets its own hair");
Demand(!WardrobeBase.TryValue("Hairstyle",9,255,new(),wornHair,out var changedHair) && changedHair==9,"Keep a later external hairstyle change");
var appearanceBytes=new byte[26];
var appliedAppearance=new JObject();
var underlying=new byte[26];
foreach(var (key,index,mask) in WardrobeBase.Appearance)
{
    byte before=(byte)(0x55&mask), after=(byte)(0xaa&mask);
    appearanceBytes[index]|=after;
    underlying[index]|=before;
    appliedAppearance[key]=new JObject{["Before"]=before,["Applied"]=after,["Base"]=false};
}
WardrobeBase.RestorePreviewAppearance(appearanceBytes,appliedAppearance);
Demand(appearanceBytes.SequenceEqual(underlying),"Remove all tracked outfit appearance before applying the preview, including packed face, lipstick and paint bits");
Console.WriteLine("Worn outfit appearance isolation checks passed.");

MigrationChecks.Run();
if(args is ["--rehearse",var configRoot])MigrationChecks.Rehearse(configRoot);
if(args is ["--stage-migration",var root,var staged])MigrationChecks.Rehearse(root,staged);
