using Picture = AetherglassUI.Picture;
using System.IO;
using System.Numerics;
using AetherglassUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow : Window, IWardrobe
{
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    public ImTextureID? HeaderMark => textures.GetFromManifestResource(typeof(WardrobeWindow).Assembly, "Aetherloom.mark.png").GetWrapOrDefault()?.Handle;
    private readonly IObjectTable objects;
    private readonly WardrobeOwnership ownership;
    private readonly CharacterPreview preview;
    private readonly WardrobeLayout layout;
    private readonly string file;
    private JObject saved=new(), design=new();
    private Dictionary<Guid,LibraryDesign> library=[];
    private long nextRefresh;
    private JObject? previewBaseLook;
    private bool LocalReady=>WardrobeStore.IsLocal(saved);
    public IReadOnlyCollection<string> Folders {get;private set;}=[];
    public IEnumerable<LibraryDesign> Designs=>library.Values;

    private readonly Dictionary<string,List<WardrobeItem>> catalogs=[];
    private readonly Dictionary<uint,WardrobeItem> known=[];
    private readonly Dictionary<(byte,byte,byte,string),WardrobeStyle[]> styles=[];
    private readonly WardrobeDye[] dyes;
    private string? previewHand;
    private JObject? projection;
    private static readonly string[] Gear=["MainHand","OffHand","Head","Body","Hands","Legs","Feet"];
    private static readonly string[] Accessories=["Ears","Neck","Wrists","RFinger","LFinger"];
    private static readonly Dictionary<string,string> Labels=new(){["MainHand"]="Main hand",["OffHand"]="Off hand",["Ears"]="Earrings",["Neck"]="Necklace",["Wrists"]="Bracelets",["RFinger"]="Right ring",["LFinger"]="Left ring"};
    public Guid Selected {get;private set;}
    public string Title=>library.GetValueOrDefault(Selected)?.Name??"";
    public string Message {get;private set;}="";
    public string[] Tags=>library.GetValueOrDefault(Selected)?.Tags??[];
    private JObject Plans= new();
    private JObject assignments=new();
    public IEnumerable<WardrobeDye> Dyes=>dyes;
    public WardrobeWindow(IDalamudPluginInterface plugin,IDataManager data,WardrobeOwnership ownership,CharacterPreview preview,ITextureProvider textures,IObjectTable objects)
        :base("Aetherloom",WindowShell.HostFlags)
    {
        locations=new(data);this.plugin=plugin;this.data=data;this.ownership=ownership;this.preview=preview;this.textures=textures;this.objects=objects;
        Size=new Vector2(1180,730);SizeCondition=ImGuiCond.FirstUseEver;
        SizeConstraints=new WindowSizeConstraints{MinimumSize=new(1000,500),MaximumSize=new(float.MaxValue)};
        file=Path.Combine(plugin.GetPluginConfigDirectory(),"wardrobe-plans.json");
        if(File.Exists(file))saved=JObject.Parse(File.ReadAllText(file));
        Plans=(JObject?)saved["Plans"]??new();assignments=(JObject?)saved["Assignments"]??new();
        dyes=data.GetExcelSheet<Stain>().Where(s=>s.RowId is >0 and <256 && s.Name.ExtractText().Length>0).OrderBy(s=>s.SubOrder)
            .Select(s=>new WardrobeDye((byte)s.RowId,s.Name.ExtractText(),0xff000000u|(s.Color&255)<<16|(s.Color&0xff00)|(s.Color>>16&255),WardrobeDye.FamilyFromShade(s.Shade))).ToArray();
        if(saved["FavoriteDyes"] is JArray favorites)FavoriteDyes.UnionWith(favorites.Values<byte>());
        if(saved["FavouriteDesigns"] is JArray starred)
            foreach(var token in starred)if(Guid.TryParse(token.ToString(),out var id))favourites.Add(id);
        layout=new(this);
        if(LocalReady)
        {
            var candidate=(JObject)saved.DeepClone();
            try {if(PrepareLocationAssignments(candidate)){Persist(candidate);saved=candidate;}}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {saved=candidate;Message="Assignment migration is active but could not be saved: "+ex.Message;}
            IndexLocal();
        }
    }
    public override void PreDraw()=>WindowShell.PushHostStyle();
    public override void PostDraw()=>WindowShell.PopHostStyle();
    public override void OnOpen(){ projection=null;Refresh(); preview.Start(); }
    public override void OnClose()=>preview.Close();
    public override void Draw()
    {
        if(Environment.TickCount64>=nextRefresh)Refresh();
        BeginWearableFrame();
        AdvanceImport();
        editorAppearance=null;
        preview.WindowOrigin=ImGui.GetWindowPos();preview.WindowSize=ImGui.GetWindowSize();
        layout.Draw(()=>IsOpen=false);
    }
    private string GlamourerDirectory=>Path.Combine(Directory.GetParent(plugin.GetPluginConfigDirectory())!.FullName,"Glamourer");
    private readonly Dictionary<Guid,(DateTime Stamp,JObject Design)> savedDesigns=[];

    private JObject? GlamourerDesign(Guid id)
    {
        var info=new FileInfo(Path.Combine(GlamourerDirectory,"designs",id+".json"));
        if(!info.Exists){savedDesigns.Remove(id);return null;}
        if(savedDesigns.TryGetValue(id,out var cached) && cached.Stamp==info.LastWriteTimeUtc)return cached.Design;
        var source=JObject.Parse(File.ReadAllText(info.FullName));
        var entry=WardrobeStore.Describe(id,info.Name,source);
        if(WardrobeStore.IsAppearanceBase(entry))savedDesigns[id]=(info.LastWriteTimeUtc,source);else savedDesigns.Remove(id);
        return source;
    }

    private void Refresh()
    {
        nextRefresh=Environment.TickCount64+1200;
        try
        {
            if(!LocalReady)MigrateFromGlamourer();
            if(IsOpen && LinkedBase.HasValue && projection!=null)
            {
                bool found=ReadBase(design,out var look,out var error);
                if(!JToken.DeepEquals(look,previewBaseLook) || projectionError!=(found?null:error))
                    projection=null;
            }
        }
        catch(Exception ex) when(ex is Dalamud.Plugin.Ipc.Exceptions.IpcError or IOException or UnauthorizedAccessException
            or InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException)
        {
            if(!LocalReady)Message="Wardrobe migration pending: "+ex.Message;
            if(LinkedBase.HasValue)projection=null;
        }
    }

    private void MigrateFromGlamourer()
    {
        // Glamourer can be disabled or still loading during a first-run Glamaholic import.
        var organization=Path.Combine(GlamourerDirectory,"design_filesystem","organization.json");
        string[] folders=[];
        if(File.Exists(organization))
        {
            var index=JObject.Parse(File.ReadAllText(organization));
            if(index["Folders"] is not JObject paths)throw new InvalidDataException("Could not read Glamourer's folder list.");
            folders=paths.Properties().Select(p=>p.Name).ToArray();
        }
        var candidate=WardrobeStore.MigrateFromDesigns(Document(),WardrobeStore.ListSavedDesigns(GlamourerDirectory),folders,GlamourerDesign);
        if(File.Exists(file) && !File.Exists(file+".pre-local.bak"))File.Copy(file,file+".pre-local.bak");
        Commit(candidate);
    }

    private void IndexLocal()
    {
        Plans=(JObject)saved["Plans"]!;
        assignments=(JObject?)saved["Assignments"]??new();
        Folders=saved["Folders"]!.ToObject<string[]>()!;
        library=Plans.Properties().Select(p=>new LibraryDesign(Guid.Parse(p.Name),p.Value.Value<string>("Name")??"Design",
            p.Value["Tags"]?.ToObject<string[]>()??[],p.Value.Value<string>("Folder")??"")).ToDictionary(d=>d.Id);
        KnownTags=library.Values.SelectMany(d=>d.Tags).Where(WardrobeLibrary.VisibleTag).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        favourites.Clear();
        if(saved["FavouriteDesigns"] is JArray stars)
            foreach(var star in stars)if(Guid.TryParse(star.ToString(),out var id))favourites.Add(id);
        referenceIds=null;assignedJobTerms=null;ruleNotes.Clear();weaponJobTerms.Clear();wearableGear.Clear();
        if(!library.ContainsKey(Selected))
            Select(Designs.OrderBy(d=>d.Name).Select(d=>d.Id).FirstOrDefault());
        else if(!JToken.DeepEquals(Plans[Selected.ToString()],design))
        {
            design=EditablePlan((JObject)Plans[Selected.ToString()]!);
            projection=null;
        }
    }

    public void Select(Guid id)
    {
        Selected=id;projection=null;
        design=Plans[id.ToString()] is JObject local?EditablePlan(local):new();
        previewHand=null;
    }
    private void Save()
    {
        if(!LocalReady || !library.ContainsKey(Selected))return;
        weaponJobTerms.Remove(Selected);wearableGear.Remove(Selected);
        projection=null;Plans[Selected.ToString()]=design.DeepClone();
        Write();
    }
    private JObject Document()
    {
        saved["FavouriteDesigns"]=new JArray(favourites.Order().Select(id=>id.ToString()));
        saved["Plans"]=Plans;saved["Assignments"]=assignments;
        return saved;
    }
    private bool PrepareLocationAssignments(JObject document)
    {
        if(document["Assignments"] is not JObject bindings || !WardrobeLocationPolicy.Migrate(bindings,locations.Canonical))return false;
        if(File.Exists(file) && !File.Exists(file+".pre-locations-v2.bak"))File.Copy(file,file+".pre-locations-v2.bak");
        return true;
    }
    private void Persist(JObject document)
    {
        PrepareLocationAssignments(document);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file+".tmp",document.ToString());File.Move(file+".tmp",file,true);
    }
    private void Write()
    {
        ruleNotes.Clear();referenceIds=null;
        try{Persist(Document());Message="";}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Message="Could not save the wardrobe edit to disk: "+ex.Message;}
    }
    private void Commit(JObject candidate)
    {
        Persist(candidate);
        saved=candidate;IndexLocal();Message="";
    }
    private bool EditLibrary(Action<JObject> edit)
    {
        if(!LocalReady){Message="Waiting for the wardrobe migration.";return false;}
        try
        {
            var candidate=(JObject)Document().DeepClone();
            edit(candidate);
            Commit(candidate);return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {Message=ex.Message;return false;}
    }
    private bool HasFolder(string folder)=>folder.Length==0 || Folders.Contains(folder);
    private static bool ValidFolderName(string name)=>!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/','\\'])<0;
    private bool AddPlan(Guid id,JObject plan,string name,string folder)
    {
        if(!HasFolder(folder) || string.IsNullOrWhiteSpace(name) || library.ContainsKey(id))return false;
        plan["Name"]=name.Trim();plan["Folder"]=folder;
        return EditLibrary(doc=>((JObject)doc["Plans"]!).Add(id.ToString(),plan));
    }
    public bool Create(string name,string folder)
    {
        var id=Guid.NewGuid();
        if(!AddPlan(id,new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject(),["Tags"]=new JArray(WardrobeLibrary.Tag)},name,folder))return false;
        Select(id);return true;
    }
    public bool CreateFolder(string parent,string name)
    {
        var path=WardrobeLibrary.PathFor(parent,name.Trim());
        if(!HasFolder(parent) || !ValidFolderName(name) || Folders.Contains(path,StringComparer.OrdinalIgnoreCase))
        {Message="Choose a unique folder name without path separators.";return false;}
        return EditLibrary(doc=>((JArray)doc["Folders"]!).Add(path));
    }
    public bool RelocateFolder(string source,string parent,string name)
        =>ValidFolderName(name) && EditLibrary(doc=>WardrobeStore.ChangeFolder(doc,source,parent,name.Trim()));
    public bool DeleteFolder(string path)
        =>EditLibrary(doc=>WardrobeStore.ChangeFolder(doc,path,WardrobeLibrary.Parent(path),null));
    public void CloneDesign(Guid source,string name)
    {
        if(Plans[source.ToString()] is not JObject original || !library.TryGetValue(source,out var entry))return;
        var id=Guid.NewGuid();
        var copy=(JObject)original.DeepClone();copy["Identifier"]=id.ToString();
        if(AddPlan(id,copy,name,entry.Folder))Select(id);
    }
    public bool RenameDesign(Guid id,string name)=>library.ContainsKey(id) && !string.IsNullOrWhiteSpace(name)
        && EditLibrary(doc=>doc["Plans"]![id.ToString()]!["Name"]=name.Trim());
    public bool DeleteDesign(Guid id)=>library.ContainsKey(id) && EditLibrary(doc=>
    {
        ((JObject)doc["Plans"]!).Remove(id.ToString());
        doc["FavouriteDesigns"]=new JArray(favourites.Where(f=>f!=id).Select(f=>f.ToString()));
        foreach(var character in ((JObject)doc["Assignments"]!).Properties().Select(p=>p.Value).OfType<JObject>())
            foreach(var section in new[]{"Rules","Jobs","Zones"})
                if(character[section] is JObject bindings)
                    foreach(var binding in bindings.Properties().Where(p=>Guid.TryParse(p.Value.ToString(),out var owner) && owner==id).ToArray())
                        binding.Remove();
    });
    public void MoveDesign(Guid id,string folder)
    {
        if(library.ContainsKey(id) && HasFolder(folder))EditLibrary(doc=>doc["Plans"]![id.ToString()]!["Folder"]=folder);
    }
    public void EditTags(string[] remove,string? add)
    {
        if(!library.ContainsKey(Selected))return;
        var removed=remove.Where(WardrobeLibrary.VisibleTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags=Tags.Where(t=>!removed.Contains(t)).ToList();
        if(add!=null && !WardrobeLibrary.Tagged(tags,add))tags.Add(add);
        EditLibrary(doc=>doc["Plans"]![Selected.ToString()]!["Tags"]=new JArray(tags));
    }
    private JObject Equipment=>(JObject)(design["Equipment"]??=new JObject());
    private JObject Look=>(JObject)(design["Customize"]??=new JObject());
    private JObject Extras=>(JObject)(design["AdditionalMainHands"]??=new JObject());
    private JObject Entry(string key)
    {
        if(key.StartsWith("OffHand"))
        {
            var pairs=(JObject)(design["PairedOffHands"]??=new JObject());
            string mainKey="MainHand"+key[7..];
            return (JObject)(pairs[mainKey]??=WardrobeWeapons.Empty());
        }
        var parent=key.Contains('/')?Extras:Equipment;
        return (JObject)(parent[key]??=new JObject());
    }
    private WardrobeItem Resolve(ulong id)
    {
        if(WardrobeWeapons.IsEmptyItem(id))return new(0,"Empty",0,0);
        if(id>uint.MaxValue||!data.GetExcelSheet<Item>().TryGetRow((uint)id,out var row))return new(0,"Custom model",0,0);
        if(!known.TryGetValue((uint)id,out var item)) known[(uint)id]=item=FromRow(row);
        return item;
    }
    public IReadOnlyList<string> KnownTags {get;private set;}=[];
    private readonly Dictionary<Guid,string[]> weaponJobTerms=[];
    private Dictionary<string,string[]>? jobNames;
    private ulong searchCharacter;
    private Dictionary<Guid,List<string>>? assignedJobTerms;
    public IEnumerable<string> WeaponTerms(Guid id)
    {
        if(!weaponJobTerms.TryGetValue(id,out var terms))
        {
            if(!library.TryGetValue(id,out var entry))return [];
            var plan=Plans[id.ToString()] as JObject;
            var ids=new List<ulong>{plan?["Equipment"]?["MainHand"]?.Value<ulong?>("ItemId")??entry.MainHand};
            if(plan?["AdditionalMainHands"] is JObject extras)
                ids.AddRange(extras.Properties().Select(p=>p.Value.Value<ulong?>("ItemId")??0UL));
            var localized=data.GetExcelSheet<ClassJob>();
            jobNames??=data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English).Where(j=>j.RowId>0)
                .Select(j=>
                {
                    var local=localized.GetRowOrDefault(j.RowId);
                    return (j.Abbreviation.ExtractText(),j.Name.ExtractText(),local?.Name.ExtractText(),local?.Abbreviation.ExtractText());
                }).ToJobAliases();
            terms=ids.SelectMany(item=>Jobs(Resolve(item).Jobs)).Distinct()
                .SelectMany(abbr=>(jobNames.GetValueOrDefault(abbr)??[]).Prepend(abbr)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            weaponJobTerms[id]=terms;
        }
        return terms;
    }
    public IEnumerable<string> JobTerms(Guid id)
    {
        var terms=WeaponTerms(id);
        if(!HasCharacter)return terms;
        if(assignedJobTerms==null || searchCharacter!=ownership.Character)
        {
            searchCharacter=ownership.Character;assignedJobTerms=[];
            var jobs=data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English);
            var local=data.GetExcelSheet<ClassJob>();
            foreach(var assignment in Rules.Properties())
            {
                var condition=assignment.Name.Split('|').Last();
                if(assignment.Value is not JValue raw || !Guid.TryParse(raw.ToString(),out var owner) || !condition.StartsWith("job:") || !uint.TryParse(condition.AsSpan(4),out var jobId)
                    || !jobs.TryGetRow(jobId,out var job))continue;
                if(!assignedJobTerms.TryGetValue(owner,out var labels))assignedJobTerms[owner]=labels=[];
                labels.Add(job.Name.ExtractText());labels.Add(job.Abbreviation.ExtractText());
                var translated=local.GetRow(jobId);labels.Add(translated.Name.ExtractText());labels.Add(translated.Abbreviation.ExtractText());
            }
        }
        return assignedJobTerms.TryGetValue(id,out var assigned)?terms.Concat(assigned):terms;
    }
    private readonly Dictionary<uint,string> eligibleJobs=[];
    private WardrobeItem FromRow(Item item)
    {
        var jobs="";
        if(item.EquipSlotCategory.Value.MainHand>0 || item.EquipSlotCategory.Value.OffHand>0)
        {
            var category=item.ClassJobCategory.Value;
            if(!eligibleJobs.TryGetValue(category.RowId,out jobs))
            {
                var labels=data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English)
                    .Where(j=>j.RowId>0).Select(j=>j.Abbreviation.ExtractText())
                    .Where(j=>typeof(ClassJobCategory).GetProperty(j)?.GetValue(category) is true);
                eligibleJobs[category.RowId]=jobs=string.Join(" / ",labels);
            }
        }
        return new(item.RowId,item.Name.ExtractText(),item.Icon,item.DyeCount,jobs);
    }
    public IEnumerable<WardrobeSlot> Slots(bool accessories)
    {
        IEnumerable<string> Keys()
        {
            foreach(var hand in Extras.Properties().Select(p=>p.Name).Prepend("MainHand"))
            {
                yield return hand;
                if(WardrobeWeapons.OffCategory(Category(Entry(hand).Value<ulong>("ItemId")))!=0)
                    yield return "OffHand"+hand[8..];
            }
            foreach(var key in Gear.Skip(2))yield return key;
        }
        foreach(var key in accessories?(IEnumerable<string>)Accessories:Keys())
        {
            var slot=Entry(key);
            var label=Labels.GetValueOrDefault(key.Split('/')[0],key);
            if(key.StartsWith("MainHand") && Resolve(slot.Value<ulong>("ItemId")).Jobs is {Length:>0} jobs)label+=" \u00b7 "+jobs;
            if(key.StartsWith("OffHand"))label+=" \u00b7 "+Resolve(Entry("MainHand"+key[7..]).Value<ulong>("ItemId")).Name;
            yield return new(key,label,Resolve(slot.Value<ulong>("ItemId")),slot.Value<byte>("Stain"),slot.Value<byte>("Stain2"),slot.Value<bool>("Apply"));
        }
    }

    private uint Category(ulong id)=>id<=uint.MaxValue && data.GetExcelSheet<Item>().TryGetRow((uint)id,out var row)?row.ItemUICategory.RowId:0;
    private bool CompatibleOff(ulong main,ulong off)
    {
        var category=WardrobeWeapons.OffCategory(Category(main));
        return category!=0 && (WardrobeWeapons.IsEmptyItem(off) || (category==Category(off) && Jobs(Resolve(main).Jobs).Intersect(Jobs(Resolve(off).Jobs)).Any()));
    }

    private JObject EditablePlan(JObject plan)
    {
        var copy=(JObject)plan.DeepClone();
        WardrobeWeapons.Upgrade(copy,CompatibleOff);
        return copy;
    }

    private JObject? PairedOff(JObject plan,string key,JObject main)
    {
        var id=main.Value<ulong>("ItemId");
        if(id>uint.MaxValue || !data.GetExcelSheet<Item>().TryGetRow((uint)id,out var item))return null;
        return WardrobeWeapons.OffHand(plan,key,main,item.ItemUICategory.RowId,item.ModelSub!=0);
    }
    public object OwnershipSnapshot=>ownership.Snapshot;
    public IEnumerable<(string Key,string Name)> WeaponJobs=>data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English)
        .Where(j=>j.RowId>0 && j.Abbreviation.ExtractText().Length>0)
        .Select(j=>(j.Abbreviation.ExtractText(),j.Name.ExtractText()));
    public IEnumerable<WardrobeItem> Catalog(string key)
    {
        var slot=key.Split('/')[0];
        if(!catalogs.TryGetValue(slot,out var items))
            catalogs[slot]=items=data.GetExcelSheet<Item>().Where(i=>i.EquipSlotCategory.RowId!=0 && WardrobeImportData.Fits(i.EquipSlotCategory.Value,slot))
                .Select(FromRow).Where(i=>i.Name.Length>0).OrderBy(i=>i.Name).ToList();
        if(slot=="OffHand")
        {
            var main=Entry("MainHand"+key[7..]).Value<ulong>("ItemId");
            return items.Where(i=>CompatibleOff(main,i.Id));
        }
        if(slot!="MainHand")return items;
        var used=Slots(false).Where(x=>x.Key!=key&&x.Key.StartsWith("MainHand")).SelectMany(x=>Jobs(x.Item.Jobs)).ToHashSet();
        return items.Where(i=>!Jobs(i.Jobs).Any(used.Contains));
    }
    private static string[] Jobs(string jobs)=>jobs.Split([' ',',','/'],StringSplitOptions.RemoveEmptyEntries);
    public Picture? Icon(uint icon)=>icon!=0 && textures.GetFromGameIcon(new GameIconLookup(icon)).TryGetWrap(out var wrap,out _)?new Picture(wrap.Handle):null;
    public string Availability(uint item)=>ownership.Availability(item);
    public void SetItem(string key,uint id)
    {
        var slot=Entry(key);slot["ItemId"]=id;slot["Apply"]=true;
        if(id==0){slot["Stain"]=0;slot["Stain2"]=0;slot["ApplyStain"]=true;}
        if(key.StartsWith("MainHand"))
        {
            if(design["PairedOffHands"] is JObject pairs && pairs[key] is JObject off && !CompatibleOff(id,off.Value<ulong>("ItemId")))pairs.Remove(key);
            previewHand=key;
        }
        Save();
    }
    public void SetDye(string key,int channel,byte dye){var slot=Entry(key);slot[channel==0?"Stain":"Stain2"]=dye;slot["ApplyStain"]=true;Save();}
    public void SetApply(string key,bool apply){var entry=Entry(key);entry["Apply"]=apply;entry["ApplyStain"]=apply;Save();}
    public void MainHand(string? remove)
    {
        if(remove==null)Extras["MainHand/"+Guid.NewGuid()]=new JObject();
        else {(design["PairedOffHands"] as JObject)?.Remove(remove);Extras.Remove(remove);if(previewHand==remove)previewHand=null;}
        Save();
    }
    private CustomizeData? editorAppearance;
    private unsafe CustomizeData EditorAppearance(Character* actor)
    {
        if(editorAppearance is {} cached)return cached;
        var c=CharacterPreview.VisibleCustomize(actor);
        PrepareProjection();
        if(projection?["Customize"] is not JObject look || projectionError!=null)return c;
        foreach(var (key,index,mask) in WardrobeBase.Appearance)
            if(WardrobeBase.TryValue(key,c.Data[index],mask,look,projection["PreviousBase"] as JObject,out var value))
                c.Data[index]=(byte)((c.Data[index]&~mask)|(value&mask));
        editorAppearance=c;return c;
    }
    public unsafe IEnumerable<WardrobeStyle> Styles(string key)
    {
        if(objects.LocalPlayer is not {} actor)return [];
        var c=EditorAppearance((Character*)actor.Address);
        if(c.Tribe==0||c.Sex>1)return [];
        var cache=(c.Tribe,c.Sex,c.Face,key);
        if(styles.TryGetValue(cache,out var found))return found;
        var entries=new List<WardrobeStyle>(); if(key=="FacePaint")entries.Add(new(0,"None",0));
        if(data.GetExcelSheet<HairMakeType>().TryGetRow((uint)((c.Tribe-1)*2+c.Sex),out var row))
        foreach(var menu in row.CharaMakeStruct)
        {
            if(menu.Customize!=(key=="Hairstyle"?6:24))continue;
            foreach(var id in menu.SubMenuParam.Take(menu.SubMenuNum))
                if(id!=uint.MaxValue && data.GetExcelSheet<CharaMakeCustomize>().TryGetRow(id,out var option) && option.FeatureID!=0
                    && (key!="Hairstyle"||c.Race!=7||option.Unknown0==0||option.Unknown0==(c.Face<=4?c.Face+4:c.Face)))
                    entries.Add(new(option.FeatureID,$"{(key=="Hairstyle"?"Hairstyle":"Pattern")} {option.FeatureID}",option.Icon));
        }
        return styles[cache]=entries.DistinctBy(x=>x.Id).ToArray();
    }
    private static string[] Fields(string group)=>group=="Hair" ? ["Hairstyle","HairColor","Highlights","HighlightsColor"] : ["FacePaint","FacePaintColor","FacePaintReversed"];
    public unsafe bool SupportsLipColor=>objects.LocalPlayer is {} player && EditorAppearance((Character*)player.Address).Race is >0 and not 7;
    public bool GroupEnabled(string group)=>group=="Lip color"?design["LipColorOverride"]?.Value<bool>("Apply")==true:Fields(group).Any(k=>Look[k]?.Value<bool>("Apply")==true);
    public void SetGroupEnabled(string group,bool enabled)
    {
        if(group=="Lip color")
        {
            design["LipColorOverride"]=new JObject{["Value"]=Style("LipColor"),["Apply"]=enabled};
            Save();return;
        }
        foreach(var key in Fields(group))
        {
            var field=(JObject)(Look[key]??=new JObject{["Value"]=Style(key)});
            field["Apply"]=enabled;
        }
        Save();
    }
    private byte[]? colors;
    public unsafe uint AppearanceColor(string key,byte value)
    {
        colors??=data.GetFile("chara/xls/charamake/human.cmp")?.Data??[];
        var actor=objects.LocalPlayer;if(actor==null)return 0xff4c4c4c;
        var c=EditorAppearance((Character*)actor.Address);
        // human.cmp contains two 2304-color global blocks followed by 1280 colors per clan/sex.
        int index=key switch{
            "HairColor"=>4608+((c.Tribe-1)*2+c.Sex)*1280+1024+value,
            "HighlightsColor"=>2304+256+value,
            "LipColor"=>value<128?512+value:1024+value-128,
            _=>value<128?640+value:1152+value-128};
        return index>=0 && (index+1)*4<=colors.Length?BitConverter.ToUInt32(colors,index*4)|0xff000000:0xff4c4c4c;
    }
    public unsafe byte Style(string key)
    {
        if(key=="LipColor")
        {
            if(design["LipColorOverride"]?.Value<byte?>("Value") is {} shade)return shade;
            return objects.LocalPlayer is {} player?EditorAppearance((Character*)player.Address).Data[20]:(byte)0;
        }
        if(Look[key]?.Value<byte?>("Value") is {} value)return value;
        if(objects.LocalPlayer is not {} actor)return 0;
        var c=EditorAppearance((Character*)actor.Address);
        return key switch{"Hairstyle"=>c.Hairstyle,"HairColor"=>c.HairColor,"HighlightsColor"=>c.HighlightsColor,
            "FacePaint"=>c.FacePaint,"FacePaintColor"=>c.FacePaintColor,"Highlights"=>(byte)(c.Highlights?128:0),
            "FacePaintReversed"=>(byte)(c.FacePaintReversed?128:0),_=>0};
    }
    public void SetStyle(string key,byte value)
    {
        var field=new JObject{["Value"]=value,["Apply"]=true};
        if(key=="LipColor")design["LipColorOverride"]=field;
        else Look[key]=field;
        Save();
    }
    public bool HasCharacter=>ownership.Character!=0;
    public ISet<byte> FavoriteDyes{get;}=new HashSet<byte>();
    public void ToggleFavorite(byte id){if(!FavoriteDyes.Remove(id))FavoriteDyes.Add(id);saved["FavoriteDyes"]=new JArray(FavoriteDyes);Write();}
    public bool? DisplayOption(string key)
    {
        if(key=="Draw")return preview.WeaponDrawn;
        var entry=Equipment[key];
        return entry?.Value<bool>("Apply")==true ? entry.Value<bool>(key=="Visor"?"IsToggled":"Show") : null;
    }
    public void ToggleDisplay(string key)
    {
        if(key=="Draw"){preview.WeaponDrawn=!preview.WeaponDrawn;return;}
        var entry=Entry(key);
        bool? next=DisplayOption(key) switch{null=>true,true=>false,false=>null};
        entry["Apply"]=next.HasValue;
        if(next.HasValue)entry[key=="Visor"?"IsToggled":"Show"]=next.Value;
        Save();
    }
    public void ResetCamera()=>preview.ResetCamera();
    public string PreviewHand
    {
        get
        {
            var hands=WardrobeWeapons.Hands(design).Where(h=>h.Slot.Value<ulong>("ItemId")!=0);
            if(previewHand!=null && hands.Any(h=>h.Key==previewHand))return previewHand;
            var job=objects.LocalPlayer is {} actor?data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English)
                .GetRowOrDefault(actor.ClassJob.RowId)?.Abbreviation.ExtractText():null;
            var matching=hands.FirstOrDefault(h=>Jobs(Resolve(h.Slot.Value<ulong>("ItemId")).Jobs).Contains(job)).Key;
            return previewHand=matching??hands.FirstOrDefault().Key??"MainHand";
        }
    }
    public void PreviewWeapon(string key){previewHand=key;projection=null;}
    private void PrepareProjection()
    {
        if(previewCharacter!=ownership.Character){previewCharacter=ownership.Character;projection=null;}
        if(projection==null && Selected!=Guid.Empty && LocalReady)
        {
            projectionError=null;
            projection=(JObject)design.DeepClone();
            try
            {
                bool found=ReadBase(design,out var baseLook,out var error);
                previewBaseLook=baseLook;
                if(found)
                {
                    projection["Customize"]=WardrobeBase.Compose(baseLook,Look);
                    if(HasCharacter && CharacterSettings["AppliedFields"] is JObject ledger)projection["PreviousBase"]=ledger.DeepClone();
                    var gear=(JObject)projection["Equipment"]!;
                    var key=PreviewHand;
                    var main=Entry(key);
                    gear["MainHand"]=main.DeepClone();
                    gear.Remove("OffHand");
                    if(PairedOff(design,key,main) is {} off)gear["OffHand"]=off;
                }
                else projectionError=error;
            }
            catch(Dalamud.Plugin.Ipc.Exceptions.IpcError){projectionError="The linked Glamourer base is unavailable.";}
        }
    }
    public void DrawPreview()
    {
        PrepareProjection();
        if(projectionError!=null){preview.Draw(null);ImGui.TextWrapped(projectionError);return;}
        preview.Draw(projection);
    }
}

