using System.Numerics;
using Dalamud.Bindings.ImGui;
using AetherglassUI;

namespace Aetherloom;

internal sealed partial class WardrobeLayout
{
    private readonly Dictionary<string,HashSet<string>> filters=WardrobeMetadata.Filters.ToDictionary(g=>g,_=>new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    private int FilterCount=>filters.Values.Count(set=>set.Count>0);
    private string optionSearch="";
    private static readonly Dictionary<string,uint> Swatches=new(StringComparer.OrdinalIgnoreCase)
    {
        ["Beige"]=0xffa6d5df,["Black"]=0xff202020,["Blue"]=0xffce754e,["Brown"]=0xff38547e,
        ["Gold"]=0xff41abd6,["Green"]=0xff598953,["Grey"]=0xff969696,["Orange"]=0xff4586de,
        ["Pink"]=0xffb79ae0,["Purple"]=0xffb56597,["Red"]=0xff4944c5,["Silver"]=0xffc1bdb7,
        ["Turquoise"]=0xffb4c347,["White"]=0xffeeeeee,["Yellow"]=0xff54d1e8
    };

    private void SidebarActions()
    {
        var right=ImGui.GetCursorPosX()+ImGui.GetContentRegionAvail().X;
        ImGui.SetWindowFontScale(1.4f); ImGui.TextUnformatted("Wardrobe"); ImGui.SetWindowFontScale(1);
        ImGui.SameLine();ImGui.SetCursorPosX(right-3*29*S-2*ImGui.GetStyle().ItemSpacing.X);
        if(IconAction("New design")){newName="";ImGui.OpenPopup("new-design");}
        ImGui.SameLine();
        if(IconAction("New folder")){folderName="";ImGui.OpenPopup("new-folder");}
        ImGui.SameLine();
        if(IconAction("Import"))ImGui.OpenPopup("wardrobe-import");
    }

    private static bool IconAction(string name,int count=0)
    {
        if(count>0)ImGui.PushStyleColor(ImGuiCol.Button,new Vector4(.14f,.43f,.42f,1));
        bool clicked=ImGui.Button("###action"+name,new Vector2((count>0?45:29)*S,27*S));
        if(count>0)ImGui.PopStyleColor();
        var p=ImGui.GetItemRectMin()+new Vector2(5.5f,4.5f)*S;
        var dl=ImGui.GetWindowDrawList();uint color=ImGui.GetColorU32(ImGuiCol.Text);
        void Line(float x,float y,float x2,float y2)=>dl.AddLine(p+new Vector2(x,y)*S,p+new Vector2(x2,y2)*S,color,1.4f*S);
        switch(name)
        {
            case "New design":
                Line(5,4,7,2);Line(7,2,9,4);Line(9,4,7,7);Line(7,7,0,13);Line(0,13,13,13);Line(13,13,7,7);
                Line(15,4,15,10);Line(12,7,18,7);break;
            case "New folder":
                Line(1,5,1,14);Line(1,5,6,5);Line(6,5,8,7);Line(8,7,14,7);Line(1,14,10,14);
                Line(15,9,15,16);Line(12,12.5f,18,12.5f);break;
            case "Import":
                Line(9,1,9,11);Line(5,7,9,11);Line(13,7,9,11);Line(2,11,2,15);Line(2,15,16,15);Line(16,15,16,11);break;
            default:
                Line(1,3,16,3);Line(16,3,10,10);Line(10,10,10,16);Line(10,16,7,14);Line(7,14,7,10);Line(7,10,1,3);
                if(count>0)dl.AddText(p+new Vector2(20,-1)*S,color,count.ToString());
                break;
        }
        if(ImGui.IsItemHovered())ImGui.SetTooltip(name);
        return clicked;
    }

    private LibraryDesign[] SearchAndFilters()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X-(FilterCount>0?45:29)*S-ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##filter","Search wardrobe",ref filter,128);
        ImGui.SameLine();
        if(IconAction("Wardrobe filters",FilterCount))ImGui.OpenPopup("filters");
        AutoOpen("filters","filters");
        var anchor=ImGui.GetItemRectMax()+new Vector2(-12,8)*S;
        bool open=WorkspaceControls.BeginPicker("filters",330,362,anchor);
        if(open)
        {
            WorkspaceControls.PickerHeading("Wardrobe filters");
            foreach(var group in WardrobeMetadata.Filters)
            {
                if(group=="Classification")ImGui.Separator();
                ImGui.PushID(group);
                ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(group);
                ImGui.SameLine(112*S);
                var chosen=filters[group];
                var summary="Any";
                if(chosen.Count>0)
                {
                    var first=chosen.First();
                    float available=ImGui.GetContentRegionAvail().X;
                    ImGui.PushStyleColor(ImGuiCol.Button,new Vector4(.13f,.35f,.34f,1));
                    if(ImGui.Button(first+"  ×",new Vector2(Math.Min(available*.68f,ImGui.CalcTextSize(first+"  ×").X+16*S),0)))
                        chosen.Remove(first);
                    ImGui.PopStyleColor();
                    if(ImGui.IsItemHovered())ImGui.SetTooltip("Remove "+first);
                    ImGui.SameLine();
                    summary=chosen.Count>1?"+"+(chosen.Count-1):"";
                }
                ImGui.SetNextItemWidth(-1);
                if(ImGui.BeginCombo("##choices",summary))
                {
                    SearchOptions();
                    if(ImGui.Selectable("Any",chosen.Count==0))chosen.Clear();
                    foreach(var option in Options(group).Where(x=>WardrobeMetadata.ContainsText(x,optionSearch)))
                        if(ImGui.Selectable(option,chosen.Contains(option),ImGuiSelectableFlags.DontClosePopups))
                            {if(!chosen.Add(option))chosen.Remove(option);}
                    ImGui.EndCombo();
                }
                ImGui.PopID();
            }
            ImGui.Separator();
            if(ImGui.SmallButton("Clear filters"))foreach(var chosen in filters.Values)chosen.Clear();
        }
        var words=filter.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        bool filtered=FilterCount>0;
        var result=wardrobe.Designs.Where(d=>words.All(word=>d.Name.Contains(word,StringComparison.OrdinalIgnoreCase)
            || d.Metadata.Any(t=>WardrobeMetadata.ContainsText(t.Value,word))
            || WardrobeMetadata.ContainsText(WardrobeMetadata.Single(WardrobeMetadata.Intended,d.Metadata),word)
            || WardrobeMetadata.ContainsText(WardrobeMetadata.Single(WardrobeMetadata.Gender,d.Metadata),word)
            || wardrobe.JobTerms(d.Id).Any(term=>term.Contains(word,StringComparison.OrdinalIgnoreCase)))
            && (!filtered || WardrobeMetadata.Matches(d.Metadata,filters,()=>wardrobe.WeaponTerms(d.Id)))).ToArray();
        if(open)
        {
            var count=$"{result.Length} matching design{(result.Length==1?"":"s")}";
            ImGui.SameLine();ImGui.SetCursorPosX(ImGui.GetCursorPosX()+Math.Max(0,ImGui.GetContentRegionAvail().X-ImGui.CalcTextSize(count).X));
            ImGui.TextDisabled(count);
            WorkspaceControls.EndPicker();
        }
        return result;
    }

    private IEnumerable<string> Options(string group)
    {
        if(group==WardrobeMetadata.Weapon)
            return wardrobe.Designs.SelectMany(d=>wardrobe.WeaponTerms(d.Id)).Where(name=>name.Length>0 && !name.All(char.IsUpper))
                .Concat(filters[group]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var existing=wardrobe.KnownTags.Where(WardrobeLibrary.VisibleTag).Select(WardrobeMetadata.Decode).Where(t=>t.Group==group).Select(t=>t.Value);
        return WardrobeMetadata.Defaults[group].Concat(existing).Concat(filters[group])
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void SearchOptions()
    {
        if(ImGui.IsWindowAppearing()){optionSearch="";ImGui.SetKeyboardFocusHere();}
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##options","Search options",ref optionSearch,80);
    }

    private void TagSummary()
    {
        var tags=wardrobe.Tags.Where(WardrobeLibrary.VisibleTag).Select(WardrobeMetadata.Decode).ToArray();
        var chips=WardrobeMetadata.Categories.SelectMany(g=>tags.Where(t=>t.Group==g).Select(t=>t.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)).ToArray();
        var jobs=wardrobe.Slots(false).Where(s=>s.Key.StartsWith("MainHand")).Select(s=>s.Item.Jobs).Where(s=>s.Length>0).Distinct();
        var descriptions=jobs.ToArray();
        var start=ImGui.GetCursorScreenPos();
        float right=start.X+ImGui.GetContentRegionAvail().X, x=start.X, height=ImGui.GetTextLineHeight()+5*S;
        var dl=ImGui.GetWindowDrawList();
        var weapon=string.Join(" / ",descriptions);
        // Keep a single line even on designs with many alternate main hands.
        if(weapon.Length>0)
        {
            float width=Math.Min(ImGui.CalcTextSize(weapon).X,(right-x)*.45f);
            dl.PushClipRect(new Vector2(x,start.Y),new Vector2(x+width,start.Y+height),true);
            dl.AddText(new Vector2(x,start.Y+2*S),0xffcbdb65,weapon);dl.PopClipRect();
            x+=width+8*S;
        }
        int shown=0;
        foreach(var chip in chips.Take(3))
        {
            if(x+ImGui.CalcTextSize(chip).X+44*S>right)break;
            Pill(chip);shown++;
        }
        if(shown<chips.Length)Pill("+"+(chips.Length-shown));
        ImGui.Dummy(new Vector2(-1,height));
        if(ImGui.IsItemHovered() && (weapon.Length>0 || chips.Length>0))
            ImGui.SetTooltip(string.Join("\n",descriptions.Concat(WardrobeMetadata.Categories.Select(g=>string.Join(", ",tags.Where(t=>t.Group==g).Select(t=>t.Value))).Where(t=>t.Length>0))));
        void Pill(string value)
        {
            float w=ImGui.CalcTextSize(value).X+12*S;
            var p=new Vector2(x,start.Y);
            dl.AddRectFilled(p,p+new Vector2(w,height),0xff302722u,5*S);
            dl.AddRect(p,p+new Vector2(w,height),0xff62564du,5*S);
            dl.AddText(p+new Vector2(6,2)*S,ImGui.GetColorU32(ImGuiCol.Text),value);
            x+=w+5*S;
        }
    }

    private void DrawDetails()
    {
        var tags=wardrobe.Tags.Where(WardrobeLibrary.VisibleTag).Select(WardrobeMetadata.Decode).ToArray();
        if(ImGui.BeginTable("audience",2,ImGuiTableFlags.SizingStretchSame))
        {
            foreach(var group in new[]{WardrobeMetadata.Gender,WardrobeMetadata.Intended})
            {
                ImGui.TableNextColumn();ImGui.PushID(group);
                bool inline=ImGui.GetContentRegionAvail().X>200*S;
                ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(group);
                if(inline)ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var current=WardrobeMetadata.Single(group,tags);
                if(ImGui.BeginCombo("##value",current))
                {
                    SearchOptions();
                    if(group==WardrobeMetadata.Gender)
                        foreach(var value in Options(group).Where(v=>WardrobeMetadata.ContainsText(v,optionSearch)))Choice(value);
                    else foreach(var (section,values) in WardrobeMetadata.Audience)
                    {
                        var matches=values.Where(v=>WardrobeMetadata.ContainsText(v,optionSearch)).ToArray();
                        if(matches.Length==0)continue;
                        ImGui.TextDisabled(section);foreach(var value in matches)Choice(value);
                    }
                    ImGui.EndCombo();
                }
                ImGui.PopID();
                void Choice(string value)
                {
                    if(ImGui.Selectable(value,current.Equals(value,StringComparison.OrdinalIgnoreCase)))
                        wardrobe.EditTags(tags.Where(t=>t.Group==group).Select(t=>t.Raw).ToArray(),
                            value==WardrobeMetadata.Defaults[group][0]?null:WardrobeMetadata.Encode(group,value));
                }
            }
            ImGui.EndTable();
        }
        foreach(var group in WardrobeMetadata.Categories)
        {
            ImGui.Spacing();ImGui.Separator();ImGui.Spacing();ImGui.PushID(group);
            ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(group);
            float left=ImGui.GetCursorPosX()+101*S;
            ImGui.SameLine();ImGui.SetCursorPosX(left);
            var selected=tags.Where(t=>t.Group==group).GroupBy(t=>t.Value,StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var set in selected)
            {
                bool dot=group=="Color" && Swatches.ContainsKey(set.Key);
                var label=(dot?"    ":"")+set.Key+"  ×";
                if(ImGui.GetCursorPosX()>left+1 && ImGui.CalcTextSize(label).X+12*S>ImGui.GetContentRegionAvail().X)
                    {ImGui.NewLine();ImGui.SetCursorPosX(left);}
                if(ImGui.Button(label))wardrobe.EditTags(set.Select(t=>t.Raw).ToArray(),null);
                if(dot)ImGui.GetWindowDrawList().AddCircleFilled(ImGui.GetItemRectMin()+new Vector2(10*S,ImGui.GetItemRectSize().Y/2),4*S,Swatches[set.Key]);
                if(ImGui.IsItemHovered())ImGui.SetTooltip("Remove "+set.Key);
                ImGui.SameLine();
            }
            if(ImGui.GetContentRegionAvail().X<22*S){ImGui.NewLine();ImGui.SetCursorPosX(left);}
            if(ImGui.Button("+")){newTag="";ImGui.OpenPopup("add");}
            AutoOpen("tag-"+group,"add");
            if(WorkspaceControls.BeginPicker("add",240,242))
            {
                WorkspaceControls.PickerHeading("Add "+(group==WardrobeMetadata.General?"tag":group.ToLowerInvariant()));
                if(ImGui.IsWindowAppearing())ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                bool enter=ImGui.InputTextWithHint("##new","Find or create",ref newTag,80,ImGuiInputTextFlags.EnterReturnsTrue);
                var typed=newTag.Trim();
                var choices=Options(group).Where(v=>!selected.Any(t=>t.Key.Equals(v,StringComparison.OrdinalIgnoreCase))
                    && v.Contains(typed,StringComparison.OrdinalIgnoreCase)).OrderByDescending(v=>v.Equals(typed,StringComparison.OrdinalIgnoreCase))
                    .ThenBy(v=>v,StringComparer.OrdinalIgnoreCase).ToArray();
                string? add=null;
                if(ImGui.BeginChild("suggestions",new Vector2(-1,Math.Clamp(choices.Length,1,5)*24*S)))
                {
                    foreach(var value in choices)if(ImGui.Selectable(value,typed.Length>0 && value==choices[0]))add=value;
                    if(choices.Length==0)ImGui.TextDisabled("No matching tags");
                }
                ImGui.EndChild();
                bool canCreate=typed.Length>0 && WardrobeLibrary.VisibleTag(typed) && !selected.Any(t=>t.Key.Equals(typed,StringComparison.OrdinalIgnoreCase));
                if(canCreate && !choices.Contains(typed,StringComparer.OrdinalIgnoreCase) && ImGui.SmallButton($"Create \"{typed}\""))add=typed;
                if(enter)add=choices.FirstOrDefault() ?? (canCreate?typed:null);
                if(add!=null){wardrobe.EditTags([],WardrobeMetadata.Encode(group,add));ImGui.CloseCurrentPopup();}
                WorkspaceControls.EndPicker();
            }
            ImGui.PopID();
        }
    }
}

