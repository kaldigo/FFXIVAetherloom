using System.Numerics;
using AetherglassUI;
using Dalamud.Bindings.ImGui;

namespace Aetherloom;

internal record WardrobeItem(uint Id,string Name,uint Icon,int Channels,string Jobs="");
internal record WardrobeDye(byte Id,string Name,uint Color,int Family)
{
    // Stain uses shade 2 for neutrals and 4 through 10 for red through special.
    public static int FamilyFromShade(int shade)=>Math.Clamp(shade-3,0,7);
}
internal record WardrobeStyle(byte Id,string Name,uint Icon);
internal record WardrobeSlot(string Key,string Label,WardrobeItem Item,byte First,byte Second,bool Apply);
internal sealed record LibraryDesign(Guid Id,string Name,string[] Tags,string Folder="",DateTimeOffset LastEdit=default);
internal static class WardrobeLibrary
{
    internal static string StoredName(string parent,string label)=>parent.Length==0?"[AL] "+label:label;
    internal static string PathFor(string parent,string label)=>parent.Length==0?StoredName(parent,label):parent+"/"+label;
    internal static bool Within(string path,string ancestor)=>path==ancestor || path.StartsWith(ancestor+"/",StringComparison.Ordinal);
    internal const string Tag="aetherloom";
    internal const string LegacyTag="ui-wardrobe";
    internal static bool Tagged(IEnumerable<string> tags,string tag)=>tags.Contains(tag,StringComparer.OrdinalIgnoreCase);
    internal static bool Managed(string path)=>path.StartsWith("[AL]",StringComparison.OrdinalIgnoreCase) && path.Split('/')[0][4..].Trim().Length>0;
    internal static string Parent(string path)=>path.LastIndexOf('/') is var split && split>=0?path[..split]:"";
    internal static string Label(string path)=>path.Contains('/')?path[(path.LastIndexOf('/')+1)..]:path[4..].TrimStart();
}
internal interface IWardrobe
{
    ImTextureID? HeaderMark => null;
    IEnumerable<LibraryDesign> Designs { get; }
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
    IReadOnlyCollection<string> Folders { get; }
    bool Create(string name,string folder);
    bool CreateFolder(string parent,string name);
    void MoveDesign(Guid id,string folder);
    bool RelocateFolder(string source,string parent,string name);
    bool DeleteFolder(string path);
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
    bool? DisplayOption(string key);
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
            if (wardrobe.HeaderMark is not {} mark) return;
            ImGui.GetWindowDrawList().AddImage(mark,p,p+new Vector2(size));
        });
        } finally { ImGui.PopStyleColor(6); }
    }
    private void Body()=>WorkspaceControls.Split("wardrobe-panes",
        new("designs",250,Sidebar,true),new("editor",1,Editor),new("character",345,PreviewPanel,true,true));

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
        if(ImGui.BeginChild("character-image",new Vector2(0,height),false,ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse))
            wardrobe.DrawPreview();
        ImGui.EndChild();
        if(ImGui.BeginPopupContextItem("preview-options",ImGuiPopupFlags.MouseButtonMiddle)){wardrobe.PreviewOptions();ImGui.EndPopup();}
        float right=ImGui.GetCursorPosX()+ImGui.GetContentRegionAvail().X-8*S;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()+8*S);
        foreach(var (key,label,paths) in PreviewActions)
        {
            if(key!="Weapon")ImGui.SameLine();
            if(key=="Draw")ImGui.SetCursorPosX(right-60*S-ImGui.GetStyle().ItemSpacing.X);
            bool previewOnly=key is "Draw" or "Reset";
            ImGui.BeginDisabled(!previewOnly && wardrobe.Selected==Guid.Empty);
            bool? state=key=="Reset"?null:wardrobe.DisplayOption(key);
            uint? tint=previewOnly?null:state switch{true=>0xff99d588u,false=>0xff8282f3u,null=>0xff968e88u};
            var hint=previewOnly?label:label+" - "+(state switch{true=>"On",false=>"Off",null=>"Disabled (keep current setting)"});
            bool clicked=WorkspaceControls.RoundAction(label,state==true,(dl,c,side,color)=>{
                foreach(var path in paths)
                    for(int i=1;i<path.Length;i++)dl.AddLine(c+side*path[i-1],c+side*path[i],color,1.8f*S);
            },tint,hint);
            ImGui.EndDisabled();
            if(clicked){if(key=="Reset")wardrobe.ResetCamera();else wardrobe.ToggleDisplay(key);}
        }
    }

    private static void Heading(string text)
    {
        ImGui.SetWindowFontScale(1.4f); ImGui.TextUnformatted(text); ImGui.SetWindowFontScale(1);
        ImGui.Dummy(new Vector2(0,5*S));
    }

    private Guid revealed;
    private string folder="";
    private string folderName="";
    private readonly HashSet<string> expanded=[];
    private string contextFolder="";
    private Vector2 contextPosition;
    private enum FolderAction { Menu, Rename, Delete }
    private FolderAction folderAction;
    private bool focusName;
    private void Sidebar()
    {
        Heading("Wardrobe");
        if(revealed!=wardrobe.Selected)
        {
            revealed=wardrobe.Selected;
            var path=wardrobe.Designs.FirstOrDefault(d=>d.Id==revealed)?.Folder??"";
            folder=WardrobeLibrary.Managed(path)?path:"";
            for(var current=folder;current.Length>0;current=WardrobeLibrary.Parent(current))expanded.Add(current);
        }
        if(!wardrobe.Folders.Contains(folder))folder="";
        float buttonWidth=(ImGui.GetContentRegionAvail().X-ImGui.GetStyle().ItemSpacing.X)/2;
        if(ImGui.Button("+ New design",new Vector2(buttonWidth,30*S))){newName="";ImGui.OpenPopup("new-design");}
        ImGui.SameLine();
        if(ImGui.Button("+ Folder",new Vector2(-1,30*S))){folderName="";ImGui.OpenPopup("new-folder");}
        if(WorkspaceControls.BeginPicker("new-design",226,136))
        {
            WorkspaceControls.PickerHeading("New design");ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##name","Design name",ref newName,80);
            if(newName.Trim().Length>0 && ImGui.Button("Create") && wardrobe.Create(newName.Trim(),folder))
                ImGui.CloseCurrentPopup();
            WorkspaceControls.EndPicker();
        }
        if(WorkspaceControls.BeginPicker("new-folder",226,136))
        {
            WorkspaceControls.PickerHeading("New folder");ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##name","Folder name",ref folderName,80);
            if(folderName.Trim().Length>0 && ImGui.Button("Create") && wardrobe.CreateFolder(folder,folderName.Trim()))
            {
                expanded.Add(folder);
                folder=folder.Length==0?"[AL] "+folderName.Trim():folder+"/"+folderName.Trim();
                expanded.Add(folder);
                ImGui.CloseCurrentPopup();
            }
            WorkspaceControls.EndPicker();
        }
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##filter","Filter names or tags",ref filter,128);
        ImGui.Spacing();ImGui.Separator();
        if(WorkspaceControls.TreeRow("root","Wardrobe root",folder.Length==0,true))folder="";
        DropOn("");
        var terms=filter.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var designs=wardrobe.Designs.Where(d=>terms.All(word=>d.Name.Contains(word,StringComparison.OrdinalIgnoreCase)
            || d.Tags.Any(t=>t.Contains(word,StringComparison.OrdinalIgnoreCase)))).OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase)
            .ToLookup(d=>WardrobeLibrary.Managed(d.Folder)?d.Folder:"");
        var folders=wardrobe.Folders.Order(StringComparer.OrdinalIgnoreCase).ToLookup(WardrobeLibrary.Parent);
        var matches=new HashSet<string>();
        if(terms.Length!=0)
            foreach(var group in designs)
                for(var ancestor=group.Key;ancestor.Length>0;ancestor=WardrobeLibrary.Parent(ancestor))matches.Add(ancestor);
        bool requestMenu=false;
        DrawBranch("");
        if(requestMenu)ImGui.OpenPopup("folder-actions");
        FolderMenu();
        if(terms.Length>0 && !designs.Any())ImGui.TextDisabled("No matching designs.");
        void DrawBranch(string parent)
        {
            foreach(var child in folders[parent])
            {
                if(terms.Length>0 && !matches.Contains(child))continue;
                var open=terms.Length>0||expanded.Contains(child);
                if(WorkspaceControls.TreeRow("folder/"+child,WardrobeLibrary.Label(child),folder==child,true,open))
                {
                    folder=child;
                    if(!expanded.Add(child))expanded.Remove(child);
                }
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    contextFolder=child;folderName=WardrobeLibrary.Label(child);
                    contextPosition=ImGui.GetMousePos();folderAction=FolderAction.Menu;requestMenu=true;
                }
                DragFolder(child);
                DropOn(child);
                if(open){ImGui.Indent(12*S);DrawBranch(child);ImGui.Unindent(12*S);}
            }
            foreach(var item in designs[parent])
            {
                if(WorkspaceControls.TreeRow(item.Id.ToString(),item.Name,item.Id==wardrobe.Selected,false))
                {wardrobe.Select(item.Id);folder=parent;}
                Drag(item);
            }
        }
        if(wardrobe.Message.Length>0)ImGui.TextWrapped(wardrobe.Message);
    }
    private void FolderMenu()
    {
        if(!WorkspaceControls.BeginPicker("folder-actions",220,168,contextPosition))return;
        if(!wardrobe.Folders.Contains(contextFolder))ImGui.CloseCurrentPopup();
        else
        {
            var label=WardrobeLibrary.Label(contextFolder);
            var parent=WardrobeLibrary.Parent(contextFolder);
            WorkspaceControls.PickerHeading(folderAction switch{FolderAction.Rename=>"Rename folder",FolderAction.Delete=>"Delete folder",_=>label});
            if(folderAction==FolderAction.Menu)
            {
                if(ImGui.Selectable("Rename folder",false,ImGuiSelectableFlags.DontClosePopups))
                    {folderAction=FolderAction.Rename;focusName=true;}
                ImGui.PushStyleColor(ImGuiCol.Text,new Vector4(.95f,.60f,.58f,1));
                if(ImGui.Selectable("Delete folder",false,ImGuiSelectableFlags.DontClosePopups))
                    folderAction=FolderAction.Delete;
                ImGui.PopStyleColor();
            }
            else if(folderAction==FolderAction.Rename)
            {
                if(focusName){ImGui.SetKeyboardFocusHere();focusName=false;}
                ImGui.SetNextItemWidth(-1);
                bool enter=ImGui.InputText("##folder-name",ref folderName,80,ImGuiInputTextFlags.EnterReturnsTrue|ImGuiInputTextFlags.AutoSelectAll);
                var name=folderName.Trim();
                ImGui.BeginDisabled(name.Length==0);
                bool save=ImGui.Button("Rename");
                ImGui.EndDisabled();
                if(name.Length>0 && (save||enter) && Relocate(contextFolder,parent,name))ImGui.CloseCurrentPopup();
                ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
            }
            else
            {
                ImGui.TextWrapped($"Delete {label}? Its designs and subfolders will move up one level.");
                if(ImGui.Button("Delete") && wardrobe.DeleteFolder(contextFolder))
                {
                    // Promoted children may acquire the managed prefix at the root.
                    string Promote(string path)
                    {
                        if(path==contextFolder)return parent;
                        var tail=path[(contextFolder.Length+1)..];
                        return parent.Length==0?"[AL] "+tail:parent+"/"+tail;
                    }
                    Remap(contextFolder,Promote);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
            }
        }
        if(folderAction!=FolderAction.Menu && wardrobe.Message.Length>0)ImGui.TextWrapped(wardrobe.Message);
        WorkspaceControls.EndPicker();
    }

    private bool Relocate(string source,string parent,string name)
    {
        if(WardrobeLibrary.Within(parent,source) || !wardrobe.RelocateFolder(source,parent,name))return false;
        var destination=WardrobeLibrary.PathFor(parent,name);
        Remap(source,path=>destination+path[source.Length..]);
        expanded.Add(parent);return true;
    }
    private void Remap(string source,Func<string,string> map)
    {
        if(WardrobeLibrary.Within(folder,source))folder=map(folder);
        foreach(var path in expanded.Where(p=>WardrobeLibrary.Within(p,source)).ToArray())
        {
            expanded.Remove(path);expanded.Add(map(path));
        }
    }
    private static void DragFolder(string path)
    {
        if(!ImGui.BeginDragDropSource())return;
        ImGui.SetDragDropPayload("AETHERLOOM_FOLDER",System.Text.Encoding.UTF8.GetBytes(path));
        ImGui.TextUnformatted(WardrobeLibrary.Label(path));ImGui.EndDragDropSource();
    }

    private static void Drag(LibraryDesign design)
    {
        if(!ImGui.BeginDragDropSource())return;
        Span<byte> id=stackalloc byte[16];
        design.Id.TryWriteBytes(id);
        ImGui.SetDragDropPayload("AETHERLOOM_DESIGN",id);
        ImGui.TextUnformatted(design.Name);
        ImGui.EndDragDropSource();
    }
    private unsafe void DropOn(string path)
    {
        if(!ImGui.BeginDragDropTarget())return;
        var payload=ImGui.AcceptDragDropPayload("AETHERLOOM_DESIGN");
        if(payload.Handle!=null && payload.DataSize==sizeof(Guid) && payload.IsDelivery())
        {
            wardrobe.MoveDesign(*(Guid*)payload.Data,path);
            expanded.Add(path);
        }
        var moved=ImGui.AcceptDragDropPayload("AETHERLOOM_FOLDER");
        if(moved.Handle!=null && moved.IsDelivery() && moved.DataSize>0)
        {
            var source=System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)moved.Data,moved.DataSize));
            if(wardrobe.Folders.Contains(source) && WardrobeLibrary.Parent(source)!=path)
                Relocate(source,path,WardrobeLibrary.Label(source));
        }
        ImGui.EndDragDropTarget();
    }

    private void Editor()
    {
        if(wardrobe.Selected==Guid.Empty){ImGui.TextWrapped("Create a design or add the aetherloom tag to a design in Glamourer.");return;}
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
        WorkspaceControls.PickerHeading("Select "+slot.Label.ToLowerInvariant()+" gear");
        ImGui.TextDisabled(wardrobe.Title);
        ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##search","Search "+slot.Label.ToLowerInvariant()+" gear",ref search,100))itemPage=0;
        float segment=(ImGui.GetContentRegionAvail().X-2*ImGui.GetStyle().ItemSpacing.X)/3;
        foreach(var mode in new[]{"All","Owned","Missing"})
        {
            if(mode!="All")ImGui.SameLine();
            bool active=itemFilter==mode;
            if(active)ImGui.PushStyleColor(ImGuiCol.Button,new Vector4(.12f,.42f,.40f,1));
            if(ImGui.Button(mode,new Vector2(segment,0))){itemFilter=mode;itemPage=0;}
            if(active)ImGui.PopStyleColor();
        }
        ImGui.BeginChild("results",new Vector2(-1,275*S),false);
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
        if(matches==0)ImGui.TextDisabled("No matching gear.");
        ImGui.EndChild();
        if(itemPage>0 && ImGui.SmallButton("Previous"))itemPage--;
        if(matches>24*(itemPage+1) && ImGui.SmallButton("Next"))itemPage++;
        WorkspaceControls.EndPicker();
    }
    private void DyePicker(WardrobeSlot slot,int channel,string popup)
    {
        if(!WorkspaceControls.BeginPicker(popup,296,350))return;
        WorkspaceControls.PickerHeading($"{slot.Label}  \u00b7  Dye {channel+1}");
        var chosen=channel==0?slot.First:slot.Second;
        var current=wardrobe.Dyes.FirstOrDefault(x=>x.Id==chosen);
        var caption=current?.Name??"No dye";
        for(int i=0;i<Families.Length;i++)
        {if(i!=0)ImGui.SameLine(0,7*S);if(WorkspaceControls.Swatch("family"+i,Families[i],new[]{"Neutral","Red","Brown","Yellow","Green","Blue","Purple","Special"}[i],i==family,26,true,i==7))family=i;}
        ImGui.Spacing();
        void Palette(IEnumerable<WardrobeDye> dyes,string prefix)
        {
            int columns=prefix=="color"?6:8;
            float cell=(ImGui.GetContentRegionAvail().X/S-3*(columns-1))/columns;
            int i=0;foreach(var dye in dyes)
            {
                if(i++%columns!=0)ImGui.SameLine(0,3*S);
                if(WorkspaceControls.Swatch(prefix+dye.Id,dye.Color,dye.Name,dye.Id==chosen,27,width:cell))
                {wardrobe.SetDye(slot.Key,channel,dye.Id);ImGui.CloseCurrentPopup();}
                if(ImGui.IsItemHovered())caption=dye.Name;
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))wardrobe.ToggleFavorite(dye.Id);
            }
        }
        Palette(wardrobe.Dyes.Where(d=>d.Family==family),"color");
        ImGui.TextUnformatted(caption);
        ImGui.Separator();ImGui.TextDisabled("Favorites");Palette(wardrobe.Dyes.Where(d=>wardrobe.FavoriteDyes.Contains(d.Id)).ToArray(),"favorite");
        if(wardrobe.FavoriteDyes.Count==0)ImGui.TextDisabled("Right-click a dye to add it here.");
        ImGui.Separator();if(ImGui.Button("Remove dye")){wardrobe.SetDye(slot.Key,channel,0);ImGui.CloseCurrentPopup();}
        WorkspaceControls.EndPicker();
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
        AutoOpen(key,key);
        if(!WorkspaceControls.BeginPicker(key,310,430))return;
        WorkspaceControls.PickerHeading(title);
        for(int i=0;i<192;i++)
        {
            var id=(byte)(i+(key=="FacePaintColor" && i>=96?32:0));
            if(i%12!=0)ImGui.SameLine(0,3*S);
            if(WorkspaceControls.Swatch("##"+id,wardrobe.AppearanceColor(key,id),id.ToString(),id==chosen,20))
            {wardrobe.SetStyle(key,id);ImGui.CloseCurrentPopup();}
        }
        WorkspaceControls.EndPicker();
    }
    private void StyleRow(string key,string title)
    {
        var styles=wardrobe.Styles(key).ToArray();var selected=wardrobe.Style(key);
        var current=styles.FirstOrDefault(x=>x.Id==selected);
        if(WorkspaceControls.Tile(key,title,current?.Name?? $"Style {selected}",wardrobe.Icon(current?.Icon??0),false,false,ImGui.GetContentRegionAvail().X))ImGui.OpenPopup(key+"-grid");
        AutoOpen(key,key+"-grid");
        if(!WorkspaceControls.BeginPicker(key+"-grid",345,325))return;
        WorkspaceControls.PickerHeading(key=="FacePaint"?"Face paint":title);
        ImGui.BeginChild("styles",new Vector2(-1,248*S),false);
        for(int i=0;i<styles.Length;i++)
        {
            var style=styles[i];if(i%4!=0)ImGui.SameLine();
            if(WorkspaceControls.Thumbnail("##"+style.Id,wardrobe.Icon(style.Icon),style.Name,selected==style.Id,69))
            {wardrobe.SetStyle(key,style.Id);ImGui.CloseCurrentPopup();}
        }
        if(styles.Length==0)ImGui.TextDisabled("No styles available for this character.");
        ImGui.EndChild();WorkspaceControls.EndPicker();
    }
}

