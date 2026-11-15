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
    private string? lastSnapshot;
    private bool disconnected;
    public IReadOnlyCollection<string> Folders {get;private set;}=[];
    public IEnumerable<LibraryDesign> Designs=>library.Values;

    private readonly Dictionary<string,List<WardrobeItem>> catalogs=[];
    private readonly Dictionary<uint,WardrobeItem> known=[];
    private readonly Dictionary<(byte,byte,byte,string),WardrobeStyle[]> styles=[];
    private readonly WardrobeDye[] dyes;
    private string previewHand="MainHand";
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
    }
    public override void PreDraw()=>WindowShell.PushHostStyle();
    public override void PostDraw()=>WindowShell.PopHostStyle();
    public override void OnOpen(){ lastSnapshot=null;Refresh(); preview.Start(); }
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
    private const string Api="Aether.Glamourer.";
    private void Refresh()
    {
        nextRefresh=Environment.TickCount64+1200;
        try
        {
            var snapshot=plugin.GetIpcSubscriber<string>(Api+"DesignLibrary").InvokeFunc();
            if(disconnected){disconnected=false;Message="";}
            if(snapshot==lastSnapshot){if(projectionError!=null)projection=null;return;}
            var root=JObject.Parse(snapshot);
            var entries=root["Designs"]!.ToObject<LibraryDesign[]>()!.DistinctBy(d=>d.Id).ToArray();
            var byId=entries.ToDictionary(x=>x.Id);
            if(Migrate(byId))
            {
                snapshot=plugin.GetIpcSubscriber<string>(Api+"DesignLibrary").InvokeFunc();
                root=JObject.Parse(snapshot);entries=root["Designs"]!.ToObject<LibraryDesign[]>()!.DistinctBy(d=>d.Id).ToArray();
            }
            bases=entries.Where(d=>WardrobeLibrary.Tagged(d.Tags,BaseTag) && !WardrobeLibrary.Tagged(d.Tags,WardrobeLibrary.Tag)).ToDictionary(d=>d.Id);
            projection=null;projectionError=null;
            KnownTags=entries.SelectMany(d=>d.Tags).Where(WardrobeLibrary.VisibleTag).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            ruleNotes.Clear();weaponJobTerms.Clear();
            wearableGear.Clear();
            var previous=library.GetValueOrDefault(Selected);
            library=entries.Where(d=>WardrobeLibrary.Tagged(d.Tags,WardrobeLibrary.Tag)).ToDictionary(d=>d.Id);
            Folders=root["Folders"]!.ToObject<string[]>()!.Where(WardrobeLibrary.Managed).ToArray();
            lastSnapshot=snapshot;
            if(!library.TryGetValue(Selected,out var selected))
                Select(Designs.OrderBy(d=>d.Name).Select(d=>d.Id).FirstOrDefault());
            else if(previous?.LastEdit!=selected.LastEdit && Plans[Selected.ToString()]==null)Select(Selected);
        }
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            disconnected=true;lastSnapshot=null;projection=null;
            Message="Update or enable the Glamourer fork to load the wardrobe.";
        }
    }
    private bool Migrate(Dictionary<Guid,LibraryDesign> remote)
    {
        bool changed=false,dirty=false;
        var pending=Plans.Properties().Where(p=>p.Value is JObject plan && !plan.Value<bool>("LibraryLinked")
            && WardrobeLibrary.Tagged(plan["Tags"]?.ToObject<string[]>()??[],WardrobeLibrary.LegacyTag)).ToArray();
        foreach(var property in pending)
        {
            if(!Guid.TryParse(property.Name,out var id))continue;
            var plan=(JObject)property.Value;
            var tags=plan["Tags"]!.ToObject<string[]>()!.Where(t=>!t.Equals(WardrobeLibrary.LegacyTag,StringComparison.OrdinalIgnoreCase))
                .Append(WardrobeLibrary.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(!remote.ContainsKey(id))
            {
                if(!Call(()=>CreateRemote(id,plan.Value<string>("Name")??"Design","",tags)))continue;
                changed=true;
            }
            else
            {
                bool success=true;
                foreach(var tag in tags.Where(t=>!WardrobeLibrary.Tagged(remote[id].Tags,t)))
                {
                    if(!Call(()=>SetTag(id,tag,true))){success=false;break;}
                    changed=true;
                }
                if(!success)continue;
            }
            plan["LibraryLinked"]=true;plan["Tags"]=new JArray(tags);dirty=true;
        }
        foreach(var entry in remote.Values.Where(d=>WardrobeLibrary.Tagged(d.Tags,WardrobeLibrary.LegacyTag)))
        {
            if(!WardrobeLibrary.Tagged(entry.Tags,WardrobeLibrary.Tag) && !Call(()=>SetTag(entry.Id,WardrobeLibrary.Tag,true)))continue;
            if(Call(()=>SetTag(entry.Id,WardrobeLibrary.LegacyTag,false)))changed=true;
        }
        if(dirty)Write();
        return changed;
    }
    private string CreateRemote(Guid id,string name,string parent,string[] tags)
        =>plugin.GetIpcSubscriber<Guid,string,string,string[],string>(Api+"CreateLinkedDesign").InvokeFunc(id,name,parent,tags);
    private string SetTag(Guid id,string tag,bool add)
        =>plugin.GetIpcSubscriber<Guid,string,bool,string>(Api+"SetDesignTag").InvokeFunc(id,tag,add);
    private bool Call(Func<string> action)
    {
        try {Message=action();return Message.Length==0;}
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError){Message="Update or enable the Glamourer fork to edit the wardrobe.";return false;}
    }
    public void Select(Guid id)
    {
        Selected=id;previewHand="MainHand";projection=null;
        if(Plans[id.ToString()] is JObject local){design=(JObject)local.DeepClone();return;}
        design=new();
        if(id==Guid.Empty)return;
        try {design=plugin.GetIpcSubscriber<Guid,JObject?>("Glamourer.GetDesignJObject").InvokeFunc(id)??new();}
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError){Message="Could not read this Glamourer design.";}
    }
    private void Save()
    {
        weaponJobTerms.Remove(Selected);
        wearableGear.Remove(Selected);
        projection=null;design["LibraryLinked"]=true;design["Tags"]=new JArray(Tags);design["Name"]=Title;Plans[Selected.ToString()]=design.DeepClone();
        Write();
    }
    private void Write()
    {
        ruleNotes.Clear();referenceSnapshot=null;
        saved["FavouriteDesigns"]=new JArray(favourites.Order().Select(id=>id.ToString()));
        saved["Plans"]=Plans;saved["Assignments"]=assignments;
        try{Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".tmp",saved.ToString());File.Move(file+".tmp",file,true);Message="";}
        catch(IOException){Message="Could not save the wardrobe edit to disk.";}
    }
    public bool Create(string name,string folder)
    {
        var id=Guid.NewGuid();
        if(!Call(()=>CreateRemote(id,name,folder,[WardrobeLibrary.Tag])))return false;
        design=new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject(),["Tags"]=new JArray(WardrobeLibrary.Tag),["LibraryLinked"]=true,["Name"]=name};
        Plans[id.ToString()]=design.DeepClone();Write();
        Refresh();Select(id);return true;
    }
    public bool CreateFolder(string parent,string name)
    {
        if(!Call(()=>plugin.GetIpcSubscriber<string,string,string>(Api+"CreateFolder").InvokeFunc(parent,WardrobeLibrary.StoredName(parent,name))))return false;
        Refresh();return true;
    }
    public bool RelocateFolder(string source,string parent,string name)
    {
        if(!Call(()=>plugin.GetIpcSubscriber<string,string,string,string>(Api+"RelocateFolder")
            .InvokeFunc(source,parent,WardrobeLibrary.StoredName(parent,name))))return false;
        Refresh();return true;
    }
    public bool DeleteFolder(string path)
    {
        if(!Call(()=>plugin.GetIpcSubscriber<string,string,string>(Api+"DissolveFolder").InvokeFunc(path,"[AL] ")))return false;
        Refresh();return true;
    }
    public void CloneDesign(Guid source,string name)
    {
        if(!library.TryGetValue(source,out var entry))return;
        var path=entry.Folder.Length==0?name:entry.Folder+"/"+name;
        Guid cloneId=Guid.Empty;
        if(!Call(()=>
        {
            var remote=plugin.GetIpcSubscriber<Guid,string?>("Glamourer.GetDesignBase64").InvokeFunc(source);
            if(string.IsNullOrEmpty(remote))return "The source design is unavailable.";
            var (code,id)=plugin.GetIpcSubscriber<string,string,(int,Guid)>("Glamourer.AddDesign").InvokeFunc(remote,path);
            cloneId=id;
            return code==0 && id!=Guid.Empty?"":$"Glamourer could not clone the design (code {code}).";
        }))return;

        if(Plans[source.ToString()] is JObject local)
        {
            var copy=(JObject)local.DeepClone();
            copy["Identifier"]=cloneId.ToString();
            copy["Name"]=name;
            copy["Tags"]=new JArray(entry.Tags);
            copy["LibraryLinked"]=true;
            Plans[cloneId.ToString()]=copy;
            Write();
            if(Message.Length>0)
            {
                var failure=Message;
                var removed=plugin.GetIpcSubscriber<Guid,int>("Glamourer.DeleteDesign");
                if(Call(()=>removed.InvokeFunc(cloneId)==0?"":"Could not remove the unfinished clone."))
                {
                    Plans.Remove(cloneId.ToString());
                    Message=failure;
                }
                else Message=failure+" "+Message;
                return;
            }
        }
        Refresh();
        Select(cloneId);
    }

    public bool RenameDesign(Guid id,string name)
    {
        if(!library.ContainsKey(id) || !Call(()=>plugin.GetIpcSubscriber<Guid,string,string>(Api+"RenameDesign").InvokeFunc(id,name)))return false;
        if(Plans[id.ToString()] is JObject local){local["Name"]=name;Write();}
        Refresh();return true;
    }
    public bool DeleteDesign(Guid id)
    {
        if(!library.ContainsKey(id) || !Call(()=>plugin.GetIpcSubscriber<Guid,string>(Api+"DeleteDesign").InvokeFunc(id)))return false;
        Plans.Remove(id.ToString());favourites.Remove(id);assignedJobTerms=null;
        foreach(var character in assignments.Properties().Select(p=>p.Value).OfType<JObject>())
            foreach(var type in character.Properties().Select(p=>p.Value).OfType<JObject>())
                foreach(var binding in type.Properties().Where(p=>p.Value is JValue v && Guid.TryParse(v.ToString(),out var owner) && owner==id).ToArray())
                    binding.Remove();
        Write();Refresh();return true;
    }
    public void MoveDesign(Guid id,string folder)
    {
        if(library.ContainsKey(id) && Call(()=>plugin.GetIpcSubscriber<Guid,string,string>(Api+"MoveDesign").InvokeFunc(id,folder)))Refresh();
    }
    public void EditTags(string[] remove,string? add)
    {
        var id=Selected;
        if(!library.TryGetValue(id,out var original))return;
        bool adding=add!=null && !WardrobeLibrary.Tagged(original.Tags,add);
        if(adding && !Call(()=>SetTag(id,add!,true)))return;
        var removed=new List<string>();
        var error="";
        foreach(var raw in remove.Where(WardrobeLibrary.VisibleTag).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(string.Equals(raw,add,StringComparison.OrdinalIgnoreCase))continue;
            if(Call(()=>SetTag(id,raw,false))){removed.Add(raw);continue;}
            error=Message;
            foreach(var prior in removed)Call(()=>SetTag(id,prior,true));
            if(adding)Call(()=>SetTag(id,add!,false));
            break;
        }
        Refresh();
        if(Plans[id.ToString()] is JObject local && library.TryGetValue(id,out var entry))
        {
            local["Tags"]=new JArray(entry.Tags);
            Write();
        }
        if(error.Length>0)Message=error;
    }
    private JObject Equipment=>(JObject)(design["Equipment"]??=new JObject());
    private JObject Look=>(JObject)(design["Customize"]??=new JObject());
    private JObject Extras=>(JObject)(design["AdditionalMainHands"]??=new JObject());
    private JObject Entry(string key)
    {
        var parent=key.Contains('/')?Extras:Equipment;
        return (JObject)(parent[key]??=new JObject());
    }
    private WardrobeItem Resolve(ulong id)
    {
        if(id==0)return new(0,"Empty",0,0);
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
        if(item.EquipSlotCategory.Value.MainHand>0)
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
        IEnumerable<string> keys=accessories?Accessories:Gear.Take(1).Concat(Extras.Properties().Select(x=>x.Name)).Concat(Gear.Skip(1));
        foreach(var key in keys)
        {
            var slot=Entry(key);
            yield return new(key,Labels.GetValueOrDefault(key.Split('/')[0],key),Resolve(slot.Value<ulong>("ItemId")),slot.Value<byte>("Stain"),slot.Value<byte>("Stain2"),slot.Value<bool>("Apply"));
        }
    }
    public IEnumerable<WardrobeItem> Catalog(string key)
    {
        var slot=key.Split('/')[0];
        if(!catalogs.TryGetValue(slot,out var items))
            catalogs[slot]=items=data.GetExcelSheet<Item>().Where(i=>i.EquipSlotCategory.RowId!=0 && WardrobeImportData.Fits(i.EquipSlotCategory.Value,slot))
                .Select(FromRow).Where(i=>i.Name.Length>0).OrderBy(i=>i.Name).ToList();
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
        if(key.StartsWith("MainHand"))previewHand=key;
        Save();
    }
    public void SetDye(string key,int channel,byte dye){var slot=Entry(key);slot[channel==0?"Stain":"Stain2"]=dye;slot["ApplyStain"]=true;Save();}
    public void SetApply(string key,bool apply){var entry=Entry(key);entry["Apply"]=apply;entry["ApplyStain"]=apply;Save();}
    public void MainHand(string? remove)
    {
        if(remove==null)Extras["MainHand/"+Guid.NewGuid()]=new JObject();
        else {Extras.Remove(remove);if(previewHand==remove)previewHand="MainHand";}
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
    public bool GroupEnabled(string group)=>Fields(group).Any(k=>Look[k]?.Value<bool>("Apply")==true);
    public void SetGroupEnabled(string group,bool enabled)
    {
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
            _=>value<128?640+value:1152+value-128};
        return index>=0 && (index+1)*4<=colors.Length?BitConverter.ToUInt32(colors,index*4)|0xff000000:0xff4c4c4c;
    }
    public unsafe byte Style(string key)
    {
        if(Look[key]?.Value<byte?>("Value") is {} value)return value;
        if(objects.LocalPlayer is not {} actor)return 0;
        var c=EditorAppearance((Character*)actor.Address);
        return key switch{"Hairstyle"=>c.Hairstyle,"HairColor"=>c.HairColor,"HighlightsColor"=>c.HighlightsColor,
            "FacePaint"=>c.FacePaint,"FacePaintColor"=>c.FacePaintColor,"Highlights"=>(byte)(c.Highlights?128:0),
            "FacePaintReversed"=>(byte)(c.FacePaintReversed?128:0),_=>0};
    }
    public void SetStyle(string key,byte value){Look[key]=new JObject{["Value"]=value,["Apply"]=true};Save();}
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
    public void WeaponSelector()
    {
        var hands=Slots(false).Where(x=>x.Key.StartsWith("MainHand")&&x.Item.Id!=0).ToArray();
        if(hands.Length>1 && ImGui.BeginCombo("Preview weapon",hands.FirstOrDefault(x=>x.Key==previewHand)?.Item.Name??"Select weapon"))
        {foreach(var hand in hands)if(ImGui.Selectable(hand.Item.Name+"###"+hand.Key,hand.Key==previewHand)){previewHand=hand.Key;projection=null;}ImGui.EndCombo();}
    }
    private void PrepareProjection()
    {
        if(previewCharacter!=ownership.Character){previewCharacter=ownership.Character;projection=null;}
        if(projection==null && Selected!=Guid.Empty && !disconnected)
        {
            projectionError=null;
            projection=(JObject)design.DeepClone();
            try
            {
                if(ReadBase(design,out var baseLook,out var error))
                {
                    projection["Customize"]=WardrobeBase.Compose(baseLook,Look);
                    if(HasCharacter && CharacterSettings["AppliedFields"] is JObject ledger)projection["PreviousBase"]=ledger.DeepClone();
                    if(previewHand!="MainHand")((JObject)projection["Equipment"]!)["MainHand"]=Entry(previewHand).DeepClone();
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

