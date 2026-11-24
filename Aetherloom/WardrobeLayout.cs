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
internal interface IWardrobe
{
    ImTextureID? HeaderMark => null;
    IEnumerable<LibraryDesign> Designs { get; }
    Guid Selected { get; }
    string Title { get; }
    string Message { get; }
    string[] Tags { get; }
    IReadOnlyList<string> KnownTags {get;}
    IEnumerable<string> JobTerms(Guid id);
    IEnumerable<string> WeaponTerms(Guid id);
    bool Wearable(Guid id);
    IEnumerable<WardrobeSlot> Slots(bool accessories);
    IEnumerable<WardrobeItem> Catalog(string slot);
    IEnumerable<(string Key,string Name)> WeaponJobs=>WardrobeMetadata.JobAliases.Select(p=>(p.Value,p.Key));
    object OwnershipSnapshot {get;}
    IEnumerable<WardrobeDye> Dyes { get; }
    IEnumerable<WardrobeStyle> Styles(string key);
    bool SupportsLipColor {get;}
    byte Style(string key);
    uint AppearanceColor(string key,byte value);
    Picture? Icon(uint icon);
    string Availability(uint item);
    void Select(Guid id);
    IReadOnlyCollection<string> Folders { get; }
    bool Create(string name,string folder);
    bool CreateFolder(string parent,string name);
    bool ImportBusy { get; }
    string ImportStatus { get; }
    void StartImport(bool fromPlates,string url,string parent);
    void MoveDesign(Guid id,string folder);
    void CloneDesign(Guid source,string name);
    bool RenameDesign(Guid id,string name);
    bool DeleteDesign(Guid id);
    bool RelocateFolder(string source,string parent,string name);
    bool DeleteFolder(string path);
    void EditTags(string[] remove,string? add);
    void SetItem(string key,uint id);
    void SetDye(string key,int channel,byte dye);
    void SetApply(string key,bool apply);
    void MainHand(string? remove);
    void SetStyle(string key,byte id);
    bool GroupEnabled(string group);
    void SetGroupEnabled(string group,bool enabled);
    bool IsFavourite(Guid id);
    void ToggleFavouriteDesign();
    bool ScanBases(bool opening)=>false;
    IEnumerable<LibraryDesign> Bases {get;}
    Guid? LinkedBase {get;}
    void SetBase(Guid? id);
    bool AssignmentMode {get;set;}
    IEnumerable<(string Key,string Name)> RuleOptions(string type);
    IEnumerable<string> DesignRules {get;}
    string RuleLabel(string key);
    Guid? RuleOwner(string key);
    string RuleNote(string key);
    bool SaveRule(string key,Guid? expected,bool remove);
    bool HasCharacter {get;}
    bool BypassOwnership {get;set;}
    void ApplyToCharacter();
    ISet<byte> FavoriteDyes {get;}
    void ToggleFavorite(byte id);
    bool? DisplayOption(string key);
    void ToggleDisplay(string key);
    void ResetCamera();
    string PreviewHand=>"MainHand";
    void PreviewWeapon(string key){}
    void DrawPreview();
}

internal sealed partial class WardrobeLayout(IWardrobe wardrobe)
{
    private readonly WindowShell shell=new(){ShowIconRail=false,ShowPageHeading=false,EdgeToEdgeBody=true};
    private string importUrl="";
    private string filter="",newName="",newTag="",search="";
    internal string Tab="Gear";
    internal string? OpenPicker;
    private string itemFilter="Owned";
    private string gearJob="",gearJobSearch="";
    private (string Key,string Name)[] gearJobs=[];
    private int family;
    private WardrobeItem[] gearResults=[];
    private object? resultOwnership;
    private string assignmentSearch="";
    private static readonly string[] Tabs=["Gear","Accessories","Appearance","Details","Assignments"];
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
            // The source artwork occupies the middle 320 pixels of a 512-pixel canvas.
            var margin=new Vector2(size*.10f);
            ImGui.GetWindowDrawList().AddImage(mark,p-margin,p+new Vector2(size)+margin,new Vector2(.18f),new Vector2(.82f));
        });
        } finally { ImGui.PopStyleColor(6); }
    }
    private void Body()=>WorkspaceControls.Split("wardrobe-panes",
        new("designs",250,Sidebar,true,true),new("editor",1,Editor),new("character",345,PreviewPanel,true,true));

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

    private Guid revealed;
    private string folder="";
    private string folderName="";
    private readonly HashSet<string> expanded=[];
    private string contextFolder="";
    private Guid contextDesign;
    private Vector2 contextPosition;
    private enum LibraryAction { Menu, Rename, Delete }
    private LibraryAction libraryAction;
    private bool focusName;
    private void Sidebar()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY()+4*S+ImGui.GetStyle().CellPadding.Y);
        ImGui.Indent(5*S);
        try { DrawSidebar(); }
        finally { ImGui.Unindent(5*S); }
    }
    private void DrawSidebar()
    {
        if(revealed!=wardrobe.Selected)
        {
            revealed=wardrobe.Selected;
            var path=wardrobe.Designs.FirstOrDefault(d=>d.Id==revealed)?.Folder??"";
            folder=WardrobeLibrary.Managed(path)?path:"";
            for(var current=folder;current.Length>0;current=WardrobeLibrary.Parent(current))expanded.Add(current);
        }
        if(!wardrobe.Folders.Contains(folder))folder="";
        SidebarActions();
        AutoOpen("import","wardrobe-import");
        if(WorkspaceControls.BeginPicker("wardrobe-import",344,300))
        {
            WorkspaceControls.PickerHeading("Import designs");
            ImGui.BeginDisabled(wardrobe.ImportBusy);
            ImGui.TextUnformatted("Eorzea Collection");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##outfit-url","Paste a glamour URL",ref importUrl,1024);
            ImGui.TextDisabled("Destination: "+(folder.Length==0?"Wardrobe root":WardrobeLibrary.Label(folder)));
            if(ImGui.Button("Import outfit"))wardrobe.StartImport(false,importUrl,folder);
            ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
            ImGui.TextUnformatted("Glamaholic");
            ImGui.TextWrapped("Copy saved plates and folders into the Glamaholic folder. Existing imports are skipped.");
            if(ImGui.Button("Import saved library"))wardrobe.StartImport(true,"","");
            ImGui.EndDisabled();
            if(wardrobe.ImportStatus.Length>0){ImGui.Spacing();ImGui.TextWrapped(wardrobe.ImportStatus);}
            WorkspaceControls.EndPicker();
        }
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
        var matching=SearchAndFilters();
        DrawModeSwitch();
        bool filtering=!string.IsNullOrWhiteSpace(filter) || FilterCount>0;
        ImGui.Spacing();ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize,8*S);
        bool visible=ImGui.BeginChild("wardrobe-library",Vector2.Zero);
        ImGui.PopStyleVar();
        try { if(visible)DrawLibrary(matching,filtering); }
        finally { ImGui.EndChild(); }
    }
    private void DrawModeSwitch()
    {
        float gap=10*S;
        ImGui.SetCursorPos(new Vector2((ImGui.GetWindowWidth()-40*S)/2,ImGui.GetCursorPosY()+4*S));
        bool automatic=wardrobe.AssignmentMode;
        bool mode=Controls.Switch("wardrobe-mode",automatic);
        if(mode!=automatic)wardrobe.AssignmentMode=mode;
        var min=ImGui.GetItemRectMin();
        var max=ImGui.GetItemRectMax();
        float textY=(min.Y+max.Y-ImGui.GetTextLineHeight())/2;
        var draw=ImGui.GetWindowDrawList();
        draw.AddText(new Vector2(min.X-gap-ImGui.CalcTextSize("Manual").X,textY),
            ImGui.GetColorU32(mode?ImGuiCol.TextDisabled:ImGuiCol.Text),"Manual");
        draw.AddText(new Vector2(max.X+gap,textY),
            ImGui.GetColorU32(mode?ImGuiCol.Text:ImGuiCol.TextDisabled),"Assignments");
        if(ImGui.IsItemHovered())ImGui.SetTooltip("Switch wardrobe mode for all characters.");
    }
    private void DrawLibrary(LibraryDesign[] matching,bool filtering)
    {
        if(WorkspaceControls.TreeRow("root","Wardrobe root",folder.Length==0,true))folder="";
        DropOn("");
        var designs=matching.OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase)
            .ToLookup(d=>WardrobeLibrary.Managed(d.Folder)?d.Folder:"");
        var folders=wardrobe.Folders.Order(StringComparer.OrdinalIgnoreCase).ToLookup(WardrobeLibrary.Parent);
        var matches=new HashSet<string>();
        if(filtering)
            foreach(var group in designs)
                for(var ancestor=group.Key;ancestor.Length>0;ancestor=WardrobeLibrary.Parent(ancestor))matches.Add(ancestor);
        bool requestMenu=false;
        DrawBranch("");
        if(requestMenu)ImGui.OpenPopup("library-actions");
        LibraryMenu();
        if(filtering && !designs.Any())ImGui.TextDisabled("No matching designs.");
        void DrawBranch(string parent)
        {
            foreach(var child in folders[parent])
            {
                if(filtering && !matches.Contains(child))continue;
                var open=filtering||expanded.Contains(child);
                if(WorkspaceControls.TreeRow("folder/"+child,WardrobeLibrary.Label(child),folder==child,true,open))
                {
                    folder=child;
                    if(!expanded.Add(child))expanded.Remove(child);
                }
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    contextDesign=Guid.Empty;contextFolder=child;folderName=WardrobeLibrary.Label(child);
                    contextPosition=ImGui.GetMousePos();libraryAction=LibraryAction.Menu;requestMenu=true;
                }
                DragFolder(child);
                DropOn(child);
                if(open){ImGui.Indent(12*S);DrawBranch(child);ImGui.Unindent(12*S);}
            }
            foreach(var item in designs[parent])
            {
                if(WorkspaceControls.TreeRow(item.Id.ToString(),item.Name,item.Id==wardrobe.Selected,false))
                {wardrobe.Select(item.Id);folder=parent;}
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    contextDesign=item.Id;folderName=item.Name;contextPosition=ImGui.GetMousePos();
                    libraryAction=LibraryAction.Menu;requestMenu=true;
                }
                Drag(item);
            }
        }
        if(wardrobe.Message.Length>0)ImGui.TextWrapped(wardrobe.Message);
    }
    private void LibraryMenu()
    {
        if(!WorkspaceControls.BeginPicker("library-actions",220,168,contextPosition))return;
        bool isDesign=contextDesign!=Guid.Empty;
        var entry=isDesign?wardrobe.Designs.FirstOrDefault(d=>d.Id==contextDesign):null;
        if(isDesign?entry==null:!wardrobe.Folders.Contains(contextFolder))ImGui.CloseCurrentPopup();
        else
        {
            var label=entry?.Name??WardrobeLibrary.Label(contextFolder);
            var kind=isDesign?"design":"folder";
            var parent=WardrobeLibrary.Parent(contextFolder);
            if(libraryAction!=LibraryAction.Menu)
                WorkspaceControls.PickerHeading(libraryAction switch{LibraryAction.Rename=>"Rename "+kind,LibraryAction.Delete=>"Delete "+kind,_=>label});
            if(libraryAction==LibraryAction.Menu)
            {
                if(ImGui.Selectable("Rename "+kind,false,ImGuiSelectableFlags.DontClosePopups))
                    {libraryAction=LibraryAction.Rename;focusName=true;}
                if(isDesign && ImGui.Selectable("Clone design"))
                {
                    var baseName=entry!.Name.Replace('/', '\uFF0F').Replace('\\','\uFF3C');
                    var stem=baseName+" (copy)";
                    var siblings=wardrobe.Designs.Where(d=>d.Folder==entry.Folder).Select(d=>d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var name=stem;
                    for(int number=2;siblings.Contains(name);number++)name=$"{baseName} (copy {number})";
                    wardrobe.CloneDesign(contextDesign,name);
                }
                ImGui.PushStyleColor(ImGuiCol.Text,new Vector4(.95f,.60f,.58f,1));
                if(ImGui.Selectable("Delete "+kind,false,ImGuiSelectableFlags.DontClosePopups))
                    libraryAction=LibraryAction.Delete;
                ImGui.PopStyleColor();
            }
            else if(libraryAction==LibraryAction.Rename)
            {
                if(focusName){ImGui.SetKeyboardFocusHere();focusName=false;}
                ImGui.SetNextItemWidth(-1);
                bool enter=ImGui.InputText("##folder-name",ref folderName,80,ImGuiInputTextFlags.EnterReturnsTrue|ImGuiInputTextFlags.AutoSelectAll);
                var name=folderName.Trim();
                ImGui.BeginDisabled(name.Length==0);
                bool save=ImGui.Button("Rename");
                ImGui.EndDisabled();
                if(name.Length>0 && (save||enter) && (isDesign?wardrobe.RenameDesign(contextDesign,name):Relocate(contextFolder,parent,name)))ImGui.CloseCurrentPopup();
                ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
            }
            else
            {
                ImGui.TextWrapped(isDesign?$"Delete {label} from Aetherloom? Its saved assignments will also be removed."
                    :$"Delete {label}? Its designs and subfolders will move up one level.");
                if(ImGui.Button("Delete") && (isDesign?wardrobe.DeleteDesign(contextDesign):wardrobe.DeleteFolder(contextFolder)))
                {
                    // Promoted children may acquire the managed prefix at the root.
                    string Promote(string path)
                    {
                        if(path==contextFolder)return parent;
                        var tail=path[(contextFolder.Length+1)..];
                        return parent.Length==0?"[AL] "+tail:parent+"/"+tail;
                    }
                    if(!isDesign)Remap(contextFolder,Promote);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
            }
        }
        if(libraryAction!=LibraryAction.Menu && wardrobe.Message.Length>0)ImGui.TextWrapped(wardrobe.Message);
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

    private Guid? assignmentEditorDesign;
    private void Editor()
    {
        if(assignmentEditorDesign.HasValue && assignmentEditorDesign!=wardrobe.Selected)
            ClearAssignmentDraft();
        assignmentEditorDesign=wardrobe.Selected;
        if(wardrobe.Selected==Guid.Empty){ImGui.TextWrapped("Create or import a wardrobe design to get started.");return;}
        const string bypassLabel="Bypass ownership";
        var start=ImGui.GetCursorScreenPos();
        var gap=ImGui.GetStyle().ItemSpacing.X;
        var controlsWidth=ImGui.CalcTextSize(bypassLabel).X+gap+40*ImGui.GetTextLineHeight()/16f;
        var labelX=start.X+ImGui.GetContentRegionAvail().X-controlsWidth;
        ImGui.PushClipRect(start,new Vector2(labelX-gap,start.Y+ImGui.GetFrameHeight()*2),true);
        float side=24*S;
        bool favourite=wardrobe.IsFavourite(wardrobe.Selected);
        if(ImGui.Button("###star-design",new Vector2(side)))wardrobe.ToggleFavouriteDesign();
        var centre=(ImGui.GetItemRectMin()+ImGui.GetItemRectMax())/2;
        var dl=ImGui.GetWindowDrawList();
        uint colour=favourite?0xff7bd7ff:ImGui.GetColorU32(ImGuiCol.TextDisabled);
        Vector2 Point(int i)
        {
            float angle=(i-2.5f)*MathF.PI/5;
            return centre+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*(i%2==0?8:3.8f)*S;
        }
        for(int i=0;i<10;i++)
        {
            if(favourite)dl.AddTriangleFilled(centre,Point(i),Point((i+1)%10),colour);
            else dl.AddLine(Point(i),Point((i+1)%10),colour,1.4f*S);
        }
        if(ImGui.IsItemHovered())ImGui.SetTooltip(favourite?"Remove from favourites":"Add to favourites");
        ImGui.SameLine();
        ImGui.SetWindowFontScale(1.4f);ImGui.TextUnformatted(wardrobe.Title);ImGui.SetWindowFontScale(1);
        ImGui.PopClipRect();
        ImGui.SameLine();ImGui.SetCursorScreenPos(new Vector2(labelX,start.Y));
        ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(bypassLabel);ImGui.SameLine();
        var bypass=Controls.Switch("ownership-bypass",wardrobe.BypassOwnership);
        if(bypass!=wardrobe.BypassOwnership)wardrobe.BypassOwnership=bypass;
        if(ImGui.IsItemHovered())ImGui.SetTooltip("Allow missing or unconfirmed gear when applying this design to your character.");
        ImGui.Dummy(new Vector2(0,5*S));
        TagSummary();
        ImGui.Spacing();
        if(ImGui.BeginTabBar("wardrobe-tabs"))
        {
            try
            {
                foreach(var tab in Tabs)
                {
                    if(!ImGui.BeginTabItem(tab, startupTab==tab?ImGuiTabItemFlags.SetSelected:0)) continue;
                    try
                    {
                        if(Tab=="Assignments" && tab!=Tab)ClearAssignmentDraft();
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
                            ImGui.Separator();
                            if(wardrobe.SupportsLipColor)
                            {
                                GroupSwitch("Lip color");
                                AppearanceColor("LipColor","Lip shade");
                            }
                            else ImGui.TextDisabled("Lip color is unavailable for this appearance.");
                        }
                        else if(tab=="Details") DrawDetails();
                        else if(tab=="Assignments") DrawAssignments();
                        else DrawEquipment(tab=="Accessories");
                    }
                    finally { ImGui.EndTabItem(); }
                }
                startupTab=null;
            }
            finally { ImGui.EndTabBar(); }
        }
        if(Tab is "Gear" or "Accessories" or "Appearance")
        {
            ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY()+8*S,ImGui.GetCursorPosY()+ImGui.GetContentRegionAvail().Y-48*S));
            ImGui.BeginDisabled(!wardrobe.HasCharacter);
            if(ImGui.Button("Apply to character"))wardrobe.ApplyToCharacter();
            ImGui.EndDisabled();
            if(Tab=="Gear"){ImGui.SameLine();ImGui.BeginDisabled();ImGui.Button("Copy to glamour plate");ImGui.EndDisabled();}
        }

    }
    private string? startupTab;
    internal void Show(string tab,string? picker=null){startupTab=tab;OpenPicker=picker;}

    private void DrawEquipment(bool accessories)
    {
        var rows=wardrobe.Slots(accessories).ToArray();
        bool multipleHands=rows.Count(r=>r.Key.StartsWith("MainHand") && r.Item.Id!=0)>1;
        for(int i=0;i<rows.Length;i++)
        {
            var slot=rows[i]; ImGui.PushID(slot.Key);
            var source=slot.Item.Id==0?"":wardrobe.Availability(slot.Item.Id);
            var width=ImGui.GetContentRegionAvail().X;
            bool eye=multipleHands && slot.Item.Id!=0 && slot.Key.StartsWith("MainHand");
            var bodyWidth=width-(eye?30*S+ImGui.GetStyle().ItemSpacing.X:0)-(30*slot.Item.Channels+34)*S-ImGui.GetStyle().ItemSpacing.X*(slot.Item.Channels+1);
            if(WorkspaceControls.Tile("item",slot.Item.Name,slot.Label+(source.Length>0?"  ·  "+source:""),wardrobe.Icon(slot.Item.Icon),false,source=="Missing",bodyWidth))
            {search="";itemFilter="Owned";ImGui.OpenPopup("items");}
            AutoOpen(slot.Key,"items"); GearPicker(slot);
            if(eye)
            {
                ImGui.SameLine();
                bool active=wardrobe.PreviewHand==slot.Key;
                if(ImGui.InvisibleButton("Preview this weapon",new Vector2(30*S,30*S)))wardrobe.PreviewWeapon(slot.Key);
                if(ImGui.IsItemHovered())ImGui.SetTooltip("Preview "+slot.Item.Name);
                var center=(ImGui.GetItemRectMin()+ImGui.GetItemRectMax())*.5f;
                var draw=ImGui.GetWindowDrawList();
                uint color=active?0xffccded0u:0xff948e84u;
                draw.AddRectFilled(ImGui.GetItemRectMin(),ImGui.GetItemRectMax(),active?0xff525d2eu:0xff302b25u,4*S);
                draw.AddRect(ImGui.GetItemRectMin(),ImGui.GetItemRectMax(),active?0xffb6b754u:0xff534e46u,4*S);
                var left=center-new Vector2(9*S,0);var right=center+new Vector2(9*S,0);
                draw.AddBezierCubic(left,left+new Vector2(5,-8)*S,right+new Vector2(-5,-8)*S,right,color,1.5f*S);
                draw.AddBezierCubic(left,left+new Vector2(5,8)*S,right+new Vector2(-5,8)*S,right,color,1.5f*S);
                draw.AddCircleFilled(center,2.7f*S,color);
            }
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
            bool followedByOff=i+1<rows.Length && rows[i+1].Key.StartsWith("OffHand");
            if((slot.Key.StartsWith("MainHand/") && !followedByOff || slot.Key.StartsWith("OffHand/")) && ImGui.SmallButton("Remove weapon"))
                wardrobe.MainHand("MainHand"+slot.Key[slot.Key.IndexOf('/')..]);
            if((slot.Key.StartsWith("MainHand") || slot.Key.StartsWith("OffHand")) &&
                (i+1==rows.Length || !(rows[i+1].Key.StartsWith("MainHand") || rows[i+1].Key.StartsWith("OffHand"))))
                if(ImGui.SmallButton("+  Main hand"))wardrobe.MainHand(null);
            ImGui.PopID();
        }
    }
    private void AutoOpen(string key,string popup){if(OpenPicker==key){ImGui.OpenPopup(popup);OpenPicker=null;}}
    private void GearPicker(WardrobeSlot slot)
    {
        float popupHeight=Math.Min(600,ImGui.GetWindowViewport().WorkSize.Y/S);
        if(!WorkspaceControls.BeginPicker("items",390,popupHeight))return;
        bool reset=ImGui.IsWindowAppearing();
        bool mainHand=slot.Key.StartsWith("MainHand");
        if(reset && mainHand)
        {
            gearJob="";
            var available=wardrobe.Catalog(slot.Key).SelectMany(i=>i.Jobs.Split([' ',',','/'],StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            gearJobs=wardrobe.WeaponJobs.Where(j=>available.Contains(j.Key)).DistinctBy(j=>j.Key)
                .OrderBy(j=>j.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        WorkspaceControls.PickerHeading("Select "+slot.Label.ToLowerInvariant()+" gear");
        ImGui.TextDisabled(wardrobe.Title);
        ImGui.SetNextItemWidth(-1);
        reset|=ImGui.InputTextWithHint("##search","Search "+slot.Label.ToLowerInvariant()+" gear",ref search,100);
        if(mainHand)
        {
            var selected=gearJobs.FirstOrDefault(j=>j.Key==gearJob);
            var caption=gearJob.Length==0?"All classes / jobs":selected.Name+" ("+selected.Key+")";
            if(ImGui.Button(caption+"###gear-job",new Vector2(-1,0))){gearJobSearch="";ImGui.OpenPopup("gear-jobs");}
            if(WorkspaceControls.BeginPicker("gear-jobs",300,330))
            {
                WorkspaceControls.PickerHeading("Class / job");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##job-search","Name or abbreviation",ref gearJobSearch,80);
                string? picked=null;
                if(ImGui.Selectable("All classes / jobs",gearJob.Length==0))picked="";
                if(ImGui.BeginChild("job-results",new Vector2(-1,230*S)))
                    foreach(var job in gearJobs.Where(j=>j.Name.Contains(gearJobSearch,StringComparison.OrdinalIgnoreCase) || j.Key.Contains(gearJobSearch,StringComparison.OrdinalIgnoreCase)))
                        if(ImGui.Selectable(job.Name+" ("+job.Key+")###"+job.Key,gearJob==job.Key))picked=job.Key;
                ImGui.EndChild();
                if(picked!=null){gearJob=picked;reset=true;ImGui.CloseCurrentPopup();}
                WorkspaceControls.EndPicker();
            }
        }
        float segment=(ImGui.GetContentRegionAvail().X-2*ImGui.GetStyle().ItemSpacing.X)/3;
        foreach(var mode in new[]{"All","Owned","Missing"})
        {
            if(mode!="All")ImGui.SameLine();
            bool active=itemFilter==mode;
            if(active)ImGui.PushStyleColor(ImGuiCol.Button,new Vector4(.12f,.42f,.40f,1));
            if(ImGui.Button(mode,new Vector2(segment,0)) && !active){itemFilter=mode;reset=true;}
            if(active)ImGui.PopStyleColor();
        }
        if(ImGui.Button("Empty slot",new Vector2(-1,0))){wardrobe.SetItem(slot.Key,0);ImGui.CloseCurrentPopup();}
        var ownership=wardrobe.OwnershipSnapshot;
        if(reset || (itemFilter!="All" && !ReferenceEquals(ownership,resultOwnership)))
        {
            var catalog=wardrobe.Catalog(slot.Key).Where(i=>i.Name.Contains(search,StringComparison.OrdinalIgnoreCase));
            if(mainHand && gearJob.Length>0)
                catalog=catalog.Where(i=>i.Jobs.Split([' ',',','/'],StringSplitOptions.RemoveEmptyEntries).Contains(gearJob,StringComparer.OrdinalIgnoreCase));
            gearResults=itemFilter switch
            {
                "Missing"=>catalog.Where(i=>wardrobe.Availability(i.Id)=="Missing").ToArray(),
                "Owned"=>catalog.Where(i=>wardrobe.Availability(i.Id) is not ("Missing" or "Unconfirmed" or "Event / Mog Station exemption")).ToArray(),
                _=>catalog.ToArray()
            };
            resultOwnership=ownership;
        }
        // Give the scrollbar the full catalogue height, but only request icons for visible tiles.
        float stride=52*S+ImGui.GetStyle().ItemSpacing.Y;
        float height=Math.Min(Math.Max(60*S,(popupHeight-183)*S-(mainHand?ImGui.GetFrameHeightWithSpacing():0)),Math.Max(1,gearResults.Length)*stride);
        ImGui.SetNextWindowContentSize(new Vector2(0,gearResults.Length*stride));
        uint? chosen=null;
        if(ImGui.BeginChild("results",new Vector2(-1,height),false))
        {
            if(reset)ImGui.SetScrollY(0);
            int first=Math.Clamp((int)(ImGui.GetScrollY()/stride)-1,0,gearResults.Length);
            int end=Math.Min(gearResults.Length,first+(int)Math.Ceiling(height/stride)+3);
            if(first<end)
                ImGui.SetCursorPosY(ImGui.GetCursorPosY()+first*stride);
            for(int i=first;i<end;i++)
            {
                var item=gearResults[i];
                var status=wardrobe.Availability(item.Id);
                if(WorkspaceControls.Tile("##"+item.Id,item.Name,status,wardrobe.Icon(item.Icon),slot.Item.Id==item.Id,status=="Missing",ImGui.GetContentRegionAvail().X))
                    chosen=item.Id;
            }
            if(gearResults.Length==0)ImGui.TextDisabled("No matching gear.");
        }
        ImGui.EndChild();
        if(chosen is {} id){wardrobe.SetItem(slot.Key,id);ImGui.CloseCurrentPopup();}
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
    private string locationType="", rulePlace="", ruleJob="any";
    private readonly HashSet<string> extraPlaces=[], extraJobs=[];
    private (Guid Design,string Key,Guid Owner)? moveRequest;
    private void ClearAssignmentDraft()
    {
        locationType=rulePlace=assignmentSearch="";
        ruleJob="any";
        extraPlaces.Clear();
        extraJobs.Clear();
        moveRequest=null;
    }
    private void DrawAssignments()
    {
        if(!wardrobe.HasCharacter){ImGui.TextUnformatted("Log in to manage assignments.");return;}
        foreach(var key in wardrobe.DesignRules.ToArray())
        {
            ImGui.PushID(key);ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(wardrobe.RuleLabel(key));ImGui.SameLine();
            if(WorkspaceControls.CloseButton("remove",22*S,"Remove assignment"))wardrobe.SaveRule(key,wardrobe.Selected,true);
            ImGui.PopID();
        }
        ImGui.Spacing();
        var previousType=locationType;
        Select("Location type",ref locationType,new[]{"Anywhere","Region","City or zone","Named area","Inn","Housing"}.Select(t=>(t,t)));
        if(previousType!=locationType){rulePlace=locationType=="Anywhere"?"any":"";extraPlaces.Clear();}
        if(locationType.Length==0)return;
        if(locationType!="Anywhere")Select("Location",ref rulePlace,wardrobe.RuleOptions(locationType),extraPlaces);
        Select("Jobs or roles",ref ruleJob,wardrobe.RuleOptions("Job"),extraJobs);
        if(rulePlace.Length==0 || ruleJob.Length==0)return;
        var places=extraPlaces.Prepend(rulePlace).Distinct().ToArray();
        var jobs=extraJobs.Prepend(ruleJob).Distinct().ToArray();
        bool multiple=places.Length*jobs.Length>1;
        foreach(var place in places)
        foreach(var job in jobs)
        {
            string condition=place+"|"+job;
            ImGui.PushID(condition);
            try { DrawCondition(condition,multiple); }
            finally { ImGui.PopID(); }
        }

        void DrawCondition(string condition,bool showLabel)
        {
            if(showLabel){ImGui.Separator();ImGui.TextUnformatted(wardrobe.RuleLabel(condition));}
            var owner=wardrobe.RuleOwner(condition);
            var note=wardrobe.RuleNote(condition);
            if(note.Length>0)ImGui.TextWrapped(note);
            if(owner==wardrobe.Selected){ImGui.TextDisabled("Assigned to this design.");return;}
            if(owner is {} other)
            {
                var name=wardrobe.Designs.FirstOrDefault(d=>d.Id==other)?.Name??"another design";
                ImGui.TextWrapped("This exact assignment belongs to "+name+".");
                if(ImGui.Button("Move assignment..."))
                {
                    moveRequest=(wardrobe.Selected,condition,other);
                    ImGui.OpenPopup("move-rule");
                }
                if(WorkspaceControls.BeginPicker("move-rule",300,140))
                {
                    WorkspaceControls.PickerHeading("Move assignment");
                    ImGui.TextWrapped($"Move {wardrobe.RuleLabel(condition)} from {name} to {wardrobe.Title}?");
                    if(ImGui.Button("Move") && moveRequest==(wardrobe.Selected,condition,other))
                    {if(wardrobe.SaveRule(condition,other,false))ClearAssignmentDraft();ImGui.CloseCurrentPopup();}
                    ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
                    WorkspaceControls.EndPicker();
                }
            }
            else if(ImGui.Button("Add assignment") && wardrobe.SaveRule(condition,null,false))ClearAssignmentDraft();
        }
        void Select(string title,ref string selected,IEnumerable<(string Key,string Name)> options,HashSet<string>? extras=null)
        {
            ImGui.PushID(title);
            var list=options as (string Key,string Name)[]??options.ToArray();
            var current=selected;var label=list.FirstOrDefault(o=>o.Key==current).Name??("Choose "+title.ToLowerInvariant());
            var selectedNames=extras is {Count:>0}
                ? list.Where(o=>o.Key==current || extras.Contains(o.Key)).Select(o=>o.Name).ToArray()
                : [];
            if(selectedNames.Length>1)label=selectedNames.Length+" selected";
            ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(title);ImGui.SameLine(120*S);
            if(ImGui.Button(label+"###selector",new Vector2(-1,0))){assignmentSearch="";ImGui.OpenPopup("choices");}
            if(selectedNames.Length>1)ImGui.TextWrapped(string.Join(", ",selectedNames));
            AutoOpen(title=="Jobs or roles"?"Jobs":title=="Location type"?"Zones":title,"choices");
            if(WorkspaceControls.BeginPicker("choices",310,340))
            {
                WorkspaceControls.PickerHeading(title);
                if(extras!=null)ImGui.TextDisabled(title=="Jobs or roles"?"Select one or more jobs or roles.":"Select one or more locations.");
                if(ImGui.IsWindowAppearing())ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                bool searched=ImGui.InputTextWithHint("##find","Search",ref assignmentSearch,90);
                string? choice=null;
                if(ImGui.BeginChild("list",new Vector2(-1,Math.Clamp(list.Length,1,9)*26*S)))
                {
                    if(searched)ImGui.SetScrollY(0);
                    foreach(var option in list.Where(o=>o.Name.Contains(assignmentSearch,StringComparison.OrdinalIgnoreCase)))
                    {
                        var name=option.Name+"###"+option.Key;
                        bool checkedOption=selected==option.Key || extras?.Contains(option.Key)==true;
                        bool clicked=extras!=null ? ImGui.Checkbox(name,ref checkedOption)
                            : ImGui.Selectable(name,checkedOption,ImGuiSelectableFlags.DontClosePopups);
                        if(clicked)choice=option.Key;
                    }
                }
                ImGui.EndChild();
                if(choice!=null)
                {
                    if(extras==null){selected=choice;ImGui.CloseCurrentPopup();}
                    else if(selected==choice){selected=extras.FirstOrDefault()??"";extras.Remove(selected);}
                    else if(selected.Length==0 || selected=="any" || choice=="any"){selected=choice;extras.Clear();}
                    else if(!extras.Remove(choice))extras.Add(choice);
                }
                if(extras!=null && ImGui.Button("Done"))ImGui.CloseCurrentPopup();
                WorkspaceControls.EndPicker();
            }
            ImGui.PopID();
        }
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
        if(!WorkspaceControls.BeginPicker(key,310,485))return;
        WorkspaceControls.PickerHeading(title);
        for(int i=0;i<192;i++)
        {
            var id=(byte)(i+((key is "FacePaintColor" or "LipColor") && i>=96?32:0));
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

