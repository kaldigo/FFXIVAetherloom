using System.Numerics;
using AetherglassUI;
using Dalamud.Bindings.ImGui;

namespace Aetherloom;

internal record WardrobeItem(uint Id,string Name,uint Icon,int Channels,string Jobs="");
internal record WardrobeDye(byte Id,string Name,uint Color,int Family);
internal record WardrobeStyle(byte Id,string Name,uint Icon);
internal record WardrobeSlot(string Key,string Label,WardrobeItem Item,byte First,byte Second,bool Apply);
internal interface IWardrobe
{
    IEnumerable<(Guid Id,string Name,string[] Tags)> Designs { get; }
    Guid Selected { get; }
    string Title { get; }
    string Message { get; }
    string[] Tags { get; }
    IEnumerable<WardrobeSlot> Slots(bool accessories);
    IEnumerable<WardrobeItem> Catalog(string slot);
    IEnumerable<WardrobeDye> Dyes { get; }
    IEnumerable<WardrobeStyle> Styles(string key);
    byte Style(string key);
    uint AppearanceColor(string key,byte value);
    Picture? Icon(uint icon);
    string Availability(uint item);
    void Select(Guid id);
    void Refresh();
    void Create(string name);
    void Tag(string tag,bool add);
    void SetItem(string key,uint id);
    void SetDye(string key,int channel,byte dye);
    void SetApply(string key,bool apply);
    void MainHand(string? remove);
    void SetStyle(string key,byte id);
    bool GroupEnabled(string group);
    void SetGroupEnabled(string group,bool enabled);
    IEnumerable<(uint Id,string Name)> AssignmentOptions(string type);
    IReadOnlyCollection<uint> Assigned(string type);
    void Assign(string type,uint id,bool enabled);
    bool HasCharacter {get;}
    ISet<byte> FavoriteDyes {get;}
    void ToggleFavorite(byte id);
    bool DisplayOption(string key);
    void ToggleDisplay(string key);
    void ResetCamera();
    void PreviewOptions();
    void DrawPreview();
}

internal sealed class WardrobeLayout(IWardrobe wardrobe)
{
    private readonly WindowShell shell=new(){ShowIconRail=false,ShowPageHeading=false,EdgeToEdgeBody=true};
    private string filter="",newName="",newTag="",search="";
    internal string Tab="Gear";
    internal string? OpenPicker;
    private string itemFilter="All";
    private int family;
    private int itemPage;
    private string assignmentSearch="";
    private static readonly string[] Tabs=["Gear","Accessories","Appearance","Assignments"];
    private static readonly uint[] Families=[0xffececec,0xff3030be,0xff4285aa,0xff55ddeb,0xff539d65,0xffd98861,0xffc075a1,0xffbba180];
    private static float S=>WorkspaceControls.Scale;

    public void Draw(Action close)
    {
        shell.Close=close;
        var teal=new Vector4(.22f,.53f,.50f,.76f);
        ImGui.PushStyleColor(ImGuiCol.TabActive,teal);
        ImGui.PushStyleColor(ImGuiCol.Tab,new Vector4(.12f,.17f,.20f,1));
        ImGui.PushStyleColor(ImGuiCol.TabHovered,new Vector4(.24f,.50f,.49f,1));
        ImGui.PushStyleColor(ImGuiCol.Header,new Vector4(.15f,.39f,.38f,.9f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,new Vector4(.4f,.88f,.80f,1));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,new Vector4(.10f,.14f,.17f,.99f));
        try {
        shell.Draw("Aetherloom","",[new Destination("wardrobe","Wardrobe","",Body)],(p,size)=>{
            var dl=ImGui.GetWindowDrawList(); var c=p+new Vector2(size/2);
            dl.AddQuadFilled(c-new Vector2(0,size*.48f),c+new Vector2(size*.28f,0),c+new Vector2(0,size*.48f),c-new Vector2(size*.28f,0),WorkspaceControls.Accent);
        });
        } finally { ImGui.PopStyleColor(6); }
    }
    private void Body()=>WorkspaceControls.Split("wardrobe-panes",
        new("designs",205,Sidebar,true),new("editor",1,Editor),new("character",345,PreviewPanel,true));

    private static readonly (string Key,string Label,Vector2[][] Paths)[] PreviewActions=[
        ("Weapon","Display sheathed arms",[[new(-.23f,-.25f),new(.16f,.14f),new(.09f,.2f),new(-.23f,-.25f)],[new(.02f,.18f),new(.2f,0)],[new(.12f,.12f),new(.24f,.24f)]]),
        ("Hat","Display headgear",[[new(-.26f,.10f),new(-.19f,-.10f),new(0,-.21f),new(.17f,-.13f),new(.24f,.10f),new(-.26f,.10f)],[new(-.25f,.17f),new(.27f,.17f)]]),
        ("Visor","Manually adjust visor",[[new(-.23f,-.13f),new(.23f,-.13f),new(.17f,.09f),new(0,.19f),new(-.17f,.09f),new(-.23f,-.13f)],[new(-.14f,-.01f),new(.14f,-.01f)]]),
        ("VieraEars","Show Viera ears",[[new(-.05f,.21f),new(-.22f,-.23f),new(-.13f,-.28f),new(0,.03f),new(.13f,-.28f),new(.22f,-.23f),new(.05f,.21f)]]),
        ("Draw","Sheath / draw weapon",[[new(-.24f,.20f),new(.22f,-.22f),new(.13f,.02f),new(-.12f,.18f)],[new(-.23f,.01f),new(-.03f,.21f)]]),
        ("Reset","Reset camera",[[new(-.22f,-.02f),new(-.10f,-.16f),new(.12f,-.16f),new(.22f,-.01f),new(.15f,.14f),new(-.03f,.18f)],[new(-.23f,-.17f),new(-.23f,-.01f),new(-.06f,-.01f)]])];
    private void PreviewPanel()
    {
        float height=Math.Max(1,ImGui.GetContentRegionAvail().Y-40*S);
        if(ImGui.BeginChild("character-image",new Vector2(-1,height),false,ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse))
            wardrobe.DrawPreview();
        ImGui.EndChild();
        if(ImGui.BeginPopupContextItem("preview-options",ImGuiPopupFlags.MouseButtonMiddle)){wardrobe.PreviewOptions();ImGui.EndPopup();}
        float right=ImGui.GetCursorPosX()+ImGui.GetContentRegionAvail().X;
        foreach(var (key,label,paths) in PreviewActions)
        {
            if(key!="Weapon")ImGui.SameLine();
            if(key=="Draw")ImGui.SetCursorPosX(right-60*S-ImGui.GetStyle().ItemSpacing.X);
            bool previewOnly=key is "Draw" or "Reset";
            ImGui.BeginDisabled(!previewOnly && wardrobe.Selected==Guid.Empty);
            bool clicked=WorkspaceControls.RoundAction(label,key!="Reset" && wardrobe.DisplayOption(key),(dl,c,side,color)=>{
                foreach(var path in paths)
                    for(int i=1;i<path.Length;i++)dl.AddLine(c+side*path[i-1],c+side*path[i],color,1.8f*S);
            });
            ImGui.EndDisabled();
            if(clicked){if(key=="Reset")wardrobe.ResetCamera();else wardrobe.ToggleDisplay(key);}
        }
    }

    private static void Heading(string text)
    {
        ImGui.SetWindowFontScale(1.4f); ImGui.TextUnformatted(text); ImGui.SetWindowFontScale(1);
        ImGui.Dummy(new Vector2(0,5*S));
    }

    private void Sidebar()
    {
        Heading("Wardrobe");
        if(ImGui.Button("+  New design",new Vector2(-1,32*S))) { newName=""; ImGui.OpenPopup("new-design"); }
        if(ImGui.BeginPopup("new-design"))
        {
            ImGui.InputTextWithHint("##name","Design name",ref newName,80);
            if(newName.Trim().Length>0 && ImGui.Button("Create")) { wardrobe.Create(newName.Trim()); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##filter","Filter names or tags",ref filter,128);
        ImGui.Spacing();
        var terms=filter.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        foreach(var (id,name,tags) in wardrobe.Designs)
        {
            if(!terms.All(word=>name.Contains(word,StringComparison.OrdinalIgnoreCase)||tags.Any(t=>t.Contains(word,StringComparison.OrdinalIgnoreCase)))) continue;
            if(ImGui.Selectable(name+"###"+id,id==wardrobe.Selected,0,new Vector2(0,30*S))) wardrobe.Select(id);
        }
        ImGui.Spacing(); if(ImGui.SmallButton("Refresh designs")) wardrobe.Refresh();
    }

    private void Editor()
    {
        if(wardrobe.Selected==Guid.Empty){ImGui.TextWrapped("Create a design or refresh designs from Glamourer.");return;}
        Heading(wardrobe.Title);
        foreach(var jobs in wardrobe.Slots(false).Where(x=>x.Key.StartsWith("MainHand")).Select(x=>x.Item.Jobs).Where(x=>x.Length>0).Distinct())
        {ImGui.TextColored(new Vector4(.41f,.86f,.79f,1),jobs); ImGui.SameLine();}
        foreach(var tag in wardrobe.Tags)
        {
            if(ImGui.SmallButton(tag+"###tag"+tag)) ImGui.OpenPopup("tag"+tag);
            if(ImGui.BeginPopup("tag"+tag)){if(ImGui.MenuItem("Remove tag")) wardrobe.Tag(tag,false); ImGui.EndPopup();}
            ImGui.SameLine();
        }
        if(ImGui.SmallButton("+ Tag")) ImGui.OpenPopup("add-tag");
        if(ImGui.BeginPopup("add-tag"))
        {
            ImGui.InputTextWithHint("##tag","Tag name",ref newTag,48);
            if(newTag.Trim().Length>0 && ImGui.Button("Add")){wardrobe.Tag(newTag.Trim(),true);newTag="";ImGui.CloseCurrentPopup();}
            ImGui.EndPopup();
        }
        ImGui.SameLine();if(ImGui.SmallButton("Preview options"))ImGui.OpenPopup("preview-menu");
        if(ImGui.BeginPopup("preview-menu")){wardrobe.PreviewOptions();ImGui.EndPopup();}
        ImGui.Spacing();
        if(ImGui.BeginTabBar("wardrobe-tabs"))
        {
            foreach(var tab in Tabs)
            {
                if(!ImGui.BeginTabItem(tab, startupTab==tab?ImGuiTabItemFlags.SetSelected:0)) continue;
                Tab=tab;
                ImGui.Spacing();
                if(tab=="Appearance")
                {
                    GroupSwitch("Hair");
                    StyleRow("Hairstyle","Hairstyle");
                    AppearanceColor("HairColor","Hair color");
                    Toggle("Highlights","Highlights"); AppearanceColor("HighlightsColor","Highlight color");
                    ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                    GroupSwitch("Face paint");
                    StyleRow("FacePaint","Pattern"); AppearanceColor("FacePaintColor","Paint color");
                    Toggle("FacePaintReversed","Mirror");
                }
                else if(tab=="Assignments") DrawAssignments();
                else DrawEquipment(tab=="Accessories");
                ImGui.EndTabItem();
            }
            startupTab=null;
            ImGui.EndTabBar();
        }
        if(Tab is "Gear" or "Accessories" or "Appearance")
        {
            ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY()+8*S,ImGui.GetCursorPosY()+ImGui.GetContentRegionAvail().Y-48*S));
            ImGui.BeginDisabled();
            ImGui.Button("Apply to character"); if(Tab=="Gear"){ImGui.SameLine();ImGui.Button("Copy to glamour plate");}
            ImGui.EndDisabled();
        }
        if(wardrobe.Message.Length>0) ImGui.TextWrapped(wardrobe.Message);
    }
    private string? startupTab;
    internal void Show(string tab,string? picker=null){startupTab=tab;OpenPicker=picker;}

    private void DrawEquipment(bool accessories)
    {
        var rows=wardrobe.Slots(accessories).ToArray();
        for(int i=0;i<rows.Length;i++)
        {
            var slot=rows[i]; ImGui.PushID(slot.Key);
            var source=slot.Item.Id==0?"":wardrobe.Availability(slot.Item.Id);
            var width=ImGui.GetContentRegionAvail().X;
            var bodyWidth=width-(30*slot.Item.Channels+34)*S-ImGui.GetStyle().ItemSpacing.X*(slot.Item.Channels+1);
            if(WorkspaceControls.Tile("item",slot.Item.Name,slot.Label+(source.Length>0?"  ·  "+source:""),wardrobe.Icon(slot.Item.Icon),false,source=="Missing",bodyWidth))
            {search="";itemFilter="All";itemPage=0;ImGui.OpenPopup("items");}
            AutoOpen(slot.Key,"items"); GearPicker(slot);
            for(int channel=0;channel<slot.Item.Channels;channel++)
            {
                var id=channel==0?slot.First:slot.Second;
                var dye=wardrobe.Dyes.FirstOrDefault(d=>d.Id==id);
                ImGui.SameLine(); var popup="dye"+channel;
                if(WorkspaceControls.Swatch(popup+"-button",dye?.Color??0xff54504e,$"Dye {channel+1} · {dye?.Name??"None"}"))
                {family=dye?.Family??0;ImGui.OpenPopup(popup);}
                if(OpenPicker==slot.Key+"-"+popup)family=dye?.Family??0;
                AutoOpen(slot.Key+"-"+popup,popup); DyePicker(slot,channel,popup);
            }
            ImGui.SameLine();
            bool apply=slot.Apply;
            if(ImGui.Checkbox("##apply",ref apply))wardrobe.SetApply(slot.Key,apply);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Apply this slot");
            if(slot.Key.StartsWith("MainHand/") && ImGui.SmallButton("Remove weapon"))wardrobe.MainHand(slot.Key);
            if(slot.Key.StartsWith("MainHand") && (i+1==rows.Length||!rows[i+1].Key.StartsWith("MainHand")))
                if(ImGui.SmallButton("+  Main hand"))wardrobe.MainHand(null);
            ImGui.PopID();
        }
    }
    private void AutoOpen(string key,string popup){if(OpenPicker==key){ImGui.OpenPopup(popup);OpenPicker=null;}}
    private void GearPicker(WardrobeSlot slot)
    {
        if(!WorkspaceControls.BeginPicker("items",346,435))return;
        Heading("Select "+slot.Label.ToLowerInvariant()+" gear");
        ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##search","Search gear",ref search,100))itemPage=0;
        foreach(var mode in new[]{"All","Owned","Missing"})
        {if(mode!="All")ImGui.SameLine();if(ImGui.RadioButton(mode,itemFilter==mode)){itemFilter=mode;itemPage=0;}}
        ImGui.BeginChild("results",new Vector2(318,275)*S,false);
        int matches=0;
        foreach(var item in wardrobe.Catalog(slot.Key).Where(i=>i.Name.Contains(search,StringComparison.OrdinalIgnoreCase)))
        {
            var status=wardrobe.Availability(item.Id);
            if(itemFilter=="Missing"&&status!="Missing" || itemFilter=="Owned" && status is "Missing" or "Unconfirmed" or "Event / Mog Station exemption")continue;
            if(matches++<itemPage*24)continue;
            if(matches>24*(itemPage+1))break;
            if(WorkspaceControls.Tile("##"+item.Id,item.Name,status,wardrobe.Icon(item.Icon),slot.Item.Id==item.Id,status=="Missing",ImGui.GetContentRegionAvail().X))
            {wardrobe.SetItem(slot.Key,item.Id);ImGui.CloseCurrentPopup();}
        }
        ImGui.EndChild();
        if(itemPage>0 && ImGui.SmallButton("Previous"))itemPage--;
        if(matches>24*(itemPage+1) && ImGui.SmallButton("Next"))itemPage++;
        ImGui.EndPopup();
    }
    private void DyePicker(WardrobeSlot slot,int channel,string popup)
    {
        if(!WorkspaceControls.BeginPicker(popup,332,335))return;
        Heading($"{slot.Label}  \u00b7  Dye channel {channel+1}");
        var chosen=channel==0?slot.First:slot.Second;
        var current=wardrobe.Dyes.FirstOrDefault(x=>x.Id==chosen);
        ImGui.TextDisabled("Selected"); ImGui.SameLine();ImGui.TextUnformatted(current?.Name??"No dye");
        ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
        for(int i=0;i<Families.Length;i++)
        {if(i!=0)ImGui.SameLine();if(WorkspaceControls.Swatch("family"+i,Families[i],new[]{"Neutral","Red","Brown","Yellow","Green","Blue","Purple","Special"}[i],i==family,25,true))family=i;}
        ImGui.Spacing();
        void Palette(IEnumerable<WardrobeDye> dyes,string prefix)
        {
            int i=0;foreach(var dye in dyes)
            {
                if(i++%8!=0)ImGui.SameLine();
                if(WorkspaceControls.Swatch(prefix+dye.Id,dye.Color,dye.Name,dye.Id==chosen,29))
                {wardrobe.SetDye(slot.Key,channel,dye.Id);ImGui.CloseCurrentPopup();}
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))wardrobe.ToggleFavorite(dye.Id);
            }
        }
        Palette(wardrobe.Dyes.Where(d=>d.Family==family),"color");
        ImGui.Spacing();
        ImGui.Separator();ImGui.TextDisabled("Favorites");Palette(wardrobe.Dyes.Where(d=>wardrobe.FavoriteDyes.Contains(d.Id)).ToArray(),"favorite");
        if(wardrobe.FavoriteDyes.Count==0)ImGui.TextDisabled("Right-click a dye to save a favourite.");
        ImGui.Spacing();if(ImGui.SmallButton("Remove dye")){wardrobe.SetDye(slot.Key,channel,0);ImGui.CloseCurrentPopup();}
        ImGui.EndPopup();
    }
    private void DrawAssignments()
    {
        if(!wardrobe.HasCharacter){ImGui.TextWrapped("Log in to manage character assignments.");return;}
        foreach(var type in new[]{"Jobs","Zones"})
        {
            Heading(type);
            var assigned=wardrobe.Assigned(type);
            if(ImGui.BeginCombo("##"+type,type=="Jobs"?"Assign job":"Assign zone"))
            {
                if(ImGui.IsWindowAppearing())assignmentSearch="";
                ImGui.InputTextWithHint("##find","Search",ref assignmentSearch,80);
                foreach(var (id,name) in wardrobe.AssignmentOptions(type).Where(x=>x.Name.Contains(assignmentSearch,StringComparison.OrdinalIgnoreCase)))
                    if(ImGui.Selectable(name+"###"+id,assigned.Contains(id)))wardrobe.Assign(type,id,true);
                ImGui.EndCombo();
            }
            if(assigned.Count>0)
            foreach(var (id,name) in wardrobe.AssignmentOptions(type).Where(x=>assigned.Contains(x.Id)))
            {
                ImGui.TextUnformatted(name);ImGui.SameLine();
                if(ImGui.SmallButton("Remove###"+type+id))wardrobe.Assign(type,id,false);
            }
            ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
        }
        ImGui.TextDisabled("Assignments are saved locally. Automatic application is pending.");
    }
    private void GroupSwitch(string group)
    {
        var enabled=wardrobe.GroupEnabled(group);
        if(ImGui.Checkbox(group+"###include"+group,ref enabled))wardrobe.SetGroupEnabled(group,enabled);
    }
    private void Toggle(string key,string title)
    {
        var enabled=wardrobe.Style(key)!=0;
        if(ImGui.Checkbox(title,ref enabled))wardrobe.SetStyle(key,enabled?(byte)128:(byte)0);
    }
    private void AppearanceColor(string key,string title)
    {
        var chosen=wardrobe.Style(key);
        ImGui.TextUnformatted(title);ImGui.SameLine();
        if(WorkspaceControls.Swatch("##"+key,wardrobe.AppearanceColor(key,chosen),title))ImGui.OpenPopup(key);
        if(!WorkspaceControls.BeginPicker(key,304,345))return;
        Heading(title);
        for(int i=0;i<192;i++)
        {
            var id=(byte)(i+(key=="FacePaintColor" && i>=96?32:0));
            if(i%12!=0)ImGui.SameLine(0,2*S);
            if(WorkspaceControls.Swatch("##"+id,wardrobe.AppearanceColor(key,id),id.ToString(),id==chosen,20))
            {wardrobe.SetStyle(key,id);ImGui.CloseCurrentPopup();}
        }
        ImGui.EndPopup();
    }
    private void StyleRow(string key,string title)
    {
        var styles=wardrobe.Styles(key).ToArray();var selected=wardrobe.Style(key);
        var current=styles.FirstOrDefault(x=>x.Id==selected);
        if(WorkspaceControls.Tile(key,title,current?.Name?? $"Style {selected}",wardrobe.Icon(current?.Icon??0),false,false,ImGui.GetContentRegionAvail().X))ImGui.OpenPopup(key+"-grid");
        AutoOpen(key,key+"-grid");
        if(!WorkspaceControls.BeginPicker(key+"-grid",345,325))return;
        Heading(title);
        ImGui.BeginChild("styles",new Vector2(320,244)*S,false);
        for(int i=0;i<styles.Length;i++)
        {
            var style=styles[i];if(i%5!=0)ImGui.SameLine();
            if(WorkspaceControls.Thumbnail("##"+style.Id,wardrobe.Icon(style.Icon),style.Name,selected==style.Id,52))
            {wardrobe.SetStyle(key,style.Id);ImGui.CloseCurrentPopup();}
        }
        ImGui.EndChild();ImGui.EndPopup();
    }
}

