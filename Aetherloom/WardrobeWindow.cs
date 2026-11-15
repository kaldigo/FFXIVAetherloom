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

internal sealed class WardrobeWindow : Window, IWardrobe
{
    private readonly IDalamudPluginInterface plugin;
    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    private readonly IObjectTable objects;
    private readonly WardrobeOwnership ownership;
    private readonly CharacterPreview preview;
    private readonly WardrobeLayout layout;
    private readonly string file;
    private JObject saved=new(), design=new();
    private Dictionary<Guid,string> names=[];
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
    public string Title=>names.GetValueOrDefault(Selected)??"";
    public string Message {get;private set;}="";
    public string[] Tags=>design["Tags"]?.Values<string>().OfType<string>().ToArray()??[];
    public IEnumerable<(Guid,string,string[])> Designs=>names.OrderBy(x=>x.Value).Select(x=>(x.Key,x.Value,Plans[x.Key.ToString()]?["Tags"]?.Values<string>().OfType<string>().ToArray()??[]));
    private JObject Plans= new();
    private JObject assignments=new();
    public IEnumerable<WardrobeDye> Dyes=>dyes;
    public WardrobeWindow(IDalamudPluginInterface plugin,IDataManager data,WardrobeOwnership ownership,CharacterPreview preview,ITextureProvider textures,IObjectTable objects)
        :base("Aetherloom",WindowShell.HostFlags)
    {
        this.plugin=plugin;this.data=data;this.ownership=ownership;this.preview=preview;this.textures=textures;this.objects=objects;
        Size=new Vector2(1180,730);SizeCondition=ImGuiCond.FirstUseEver;
        SizeConstraints=new WindowSizeConstraints{MinimumSize=new(840,500),MaximumSize=new(float.MaxValue)};
        file=Path.Combine(plugin.GetPluginConfigDirectory(),"wardrobe-plans.json");
        if(File.Exists(file))saved=JObject.Parse(File.ReadAllText(file));
        Plans=(JObject?)saved["Plans"]??new();assignments=(JObject?)saved["Assignments"]??new();
        dyes=data.GetExcelSheet<Stain>().Where(s=>s.RowId is >0 and <256 && s.Name.ExtractText().Length>0).OrderBy(s=>s.SubOrder)
            .Select(s=>new WardrobeDye((byte)s.RowId,s.Name.ExtractText(),0xff000000u|(s.Color&255)<<16|(s.Color&0xff00)|(s.Color>>16&255),Math.Clamp(s.Shade-1,0,7))).ToArray();
        if(saved["FavoriteDyes"] is JArray favorites)FavoriteDyes.UnionWith(favorites.Values<byte>());
        layout=new(this);
    }
    public override void PreDraw()=>WindowShell.PushHostStyle();
    public override void PostDraw()=>WindowShell.PopHostStyle();
    public override void OnOpen(){ Refresh(); preview.Start(Selected!=Guid.Empty); }
    public override void OnClose()=>preview.Close();
    public override void Draw()=>layout.Draw(()=>IsOpen=false);
    public void Refresh()
    {
        Message="";
        try {names=plugin.GetIpcSubscriber<Dictionary<Guid,string>>("Glamourer.GetDesignList.V2").InvokeFunc();}
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError){names=[];Message="Enable Glamourer to load its designs.";}
        foreach(var plan in Plans.Properties())
            if(Guid.TryParse(plan.Name,out var id))names[id]=plan.Value.Value<string>("Name")??names.GetValueOrDefault(id)??"Design";
        Select(names.ContainsKey(Selected)?Selected:names.OrderBy(x=>x.Value).Select(x=>x.Key).FirstOrDefault());
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
        projection=null;design["Name"]=Title;Plans[Selected.ToString()]=design.DeepClone();
        Write();
    }
    private void Write()
    {
        saved["Plans"]=Plans;saved["Assignments"]=assignments;
        try{Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".tmp",saved.ToString());File.Move(file+".tmp",file,true);Message="";}
        catch(IOException){Message="Could not save the wardrobe edit to disk.";}
    }
    public void Create(string name)
    {
        var id=Guid.NewGuid();names[id]=name;Selected=id;previewHand="MainHand";
        design=new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject(),["Tags"]=new JArray("ui-wardrobe")};Save();
    }
    public void Tag(string tag,bool add)
    {
        var tags=Tags.ToList();tags.RemoveAll(t=>t.Equals(tag,StringComparison.OrdinalIgnoreCase));if(add)tags.Add(tag);
        design["Tags"]=new JArray(tags);Save();
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
        if(id>uint.MaxValue||!data.GetExcelSheet<Item>().TryGetRow((uint)id,out var row))return new(0,"Empty / custom model",0,0);
        if(!known.TryGetValue((uint)id,out var item)) known[(uint)id]=item=FromRow(row);
        return item;
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
    private static bool Fits(EquipSlotCategory e,string key)=>key switch{
        "MainHand"=>e.MainHand>0,"OffHand"=>e.OffHand>0,"Head"=>e.Head>0,"Body"=>e.Body>0,"Hands"=>e.Gloves>0,
        "Legs"=>e.Legs>0,"Feet"=>e.Feet>0,"Ears"=>e.Ears>0,"Neck"=>e.Neck>0,"Wrists"=>e.Wrists>0,"RFinger"=>e.FingerR>0,"LFinger"=>e.FingerL>0,_=>false};
    public IEnumerable<WardrobeItem> Catalog(string key)
    {
        var slot=key.Split('/')[0];
        if(!catalogs.TryGetValue(slot,out var items))
            catalogs[slot]=items=data.GetExcelSheet<Item>().Where(i=>i.EquipSlotCategory.RowId!=0 && Fits(i.EquipSlotCategory.Value,slot))
                .Select(FromRow).Where(i=>i.Name.Length>0).OrderBy(i=>i.Name).ToList();
        if(slot!="MainHand")return items;
        var used=Slots(false).Where(x=>x.Key!=key&&x.Key.StartsWith("MainHand")).SelectMany(x=>Jobs(x.Item.Jobs)).ToHashSet();
        return items.Where(i=>!Jobs(i.Jobs).Any(used.Contains));
    }
    private static string[] Jobs(string jobs)=>jobs.Split([' ',',','/'],StringSplitOptions.RemoveEmptyEntries);
    public Picture? Icon(uint icon)=>icon!=0 && textures.GetFromGameIcon(new GameIconLookup(icon)).TryGetWrap(out var wrap,out _)?new Picture(wrap.Handle):null;
    public string Availability(uint item)=>ownership.Availability(item);
    public void SetItem(string key,uint id){var slot=Entry(key);slot["ItemId"]=id;slot["Apply"]=true;if(key.StartsWith("MainHand"))previewHand=key;Save();}
    public void SetDye(string key,int channel,byte dye){var slot=Entry(key);slot[channel==0?"Stain":"Stain2"]=dye;slot["ApplyStain"]=true;Save();}
    public void SetApply(string key,bool apply){var entry=Entry(key);entry["Apply"]=apply;entry["ApplyStain"]=apply;Save();}
    public void MainHand(string? remove)
    {
        if(remove==null)Extras["MainHand/"+Guid.NewGuid()]=new JObject();
        else {Extras.Remove(remove);if(previewHand==remove)previewHand="MainHand";}
        Save();
    }
    public unsafe IEnumerable<WardrobeStyle> Styles(string key)
    {
        if(objects.LocalPlayer is not {} actor)return [];
        var c=((Character*)actor.Address)->DrawData.CustomizeData;
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
        var c=((Character*)actor.Address)->DrawData.CustomizeData;
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
        var c=((Character*)actor.Address)->DrawData.CustomizeData;
        return key switch{"Hairstyle"=>c.Hairstyle,"HairColor"=>c.HairColor,"HighlightsColor"=>c.HighlightsColor,
            "FacePaint"=>c.FacePaint,"FacePaintColor"=>c.FacePaintColor,"Highlights"=>(byte)(c.Highlights?128:0),
            "FacePaintReversed"=>(byte)(c.FacePaintReversed?128:0),_=>0};
    }
    public void SetStyle(string key,byte value){Look[key]=new JObject{["Value"]=value,["Apply"]=true};Save();}
    public bool HasCharacter=>ownership.Character!=0;
    private JObject Bindings(string type)
    {
        var character=(JObject)(assignments[ownership.Character.ToString()]??=new JObject());
        return (JObject)(character[type]??=new JObject());
    }
    private readonly Dictionary<string,(uint,string)[]> assignmentOptions=[];
    public IEnumerable<(uint Id,string Name)> AssignmentOptions(string type)
    {
        if(assignmentOptions.TryGetValue(type,out var values))return values;
        var list=type=="Jobs"?data.GetExcelSheet<ClassJob>().Where(j=>j.RowId>0).Select(j=>(j.RowId,j.Name.ExtractText())):
            data.GetExcelSheet<TerritoryType>().Where(t=>t.PlaceName.RowId!=0).Select(t=>(t.RowId,t.PlaceName.Value.Name.ExtractText()));
        return assignmentOptions[type]=list.OrderBy(x=>x.Item2).ToArray();
    }
    public IReadOnlyCollection<uint> Assigned(string type)=>Bindings(type).Properties().Where(p=>(string?)p.Value==Selected.ToString()).Select(p=>uint.Parse(p.Name)).ToArray();
    public void Assign(string type,uint id,bool enabled)
    {
        if(!HasCharacter)return;
        if(enabled)Bindings(type)[id.ToString()]=Selected.ToString();else Bindings(type).Remove(id.ToString()); Write();
    }
    public ISet<byte> FavoriteDyes{get;}=new HashSet<byte>();
    public void ToggleFavorite(byte id){if(!FavoriteDyes.Remove(id))FavoriteDyes.Add(id);saved["FavoriteDyes"]=new JArray(FavoriteDyes);Write();}
    public void DrawPreview()
    {
        var hands=Slots(false).Where(x=>x.Key.StartsWith("MainHand")&&x.Item.Id!=0).ToArray();
        if(hands.Length>1 && ImGui.BeginCombo("Weapon",hands.FirstOrDefault(x=>x.Key==previewHand)?.Item.Name??"Select weapon"))
        {foreach(var hand in hands)if(ImGui.Selectable(hand.Item.Name+"###"+hand.Key,hand.Key==previewHand)){previewHand=hand.Key;projection=null;}ImGui.EndCombo();}
        if(projection==null && Selected!=Guid.Empty)
        {
            projection=(JObject)design.DeepClone();
            if(previewHand!="MainHand")((JObject)projection["Equipment"]!)["MainHand"]=Entry(previewHand).DeepClone();
        }
        preview.Draw(projection);
    }
}

