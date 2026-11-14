using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface plugin;
    private readonly ICommandManager commands;
    private readonly WardrobeOwnership ownership;
    private readonly WindowSystem windows = new("Aetherloom");
    private readonly WardrobeWindow window;

    public Plugin(IDalamudPluginInterface plugin, ICommandManager commands, IFramework framework,
        IPlayerState player, IClientState client, IDataManager data, IPluginLog log)
    {
        this.plugin = plugin;
        this.commands = commands;
        ownership = new(framework, player, client, plugin, data, log);
        window = new(plugin, data, ownership);
        windows.AddWindow(window);
        plugin.UiBuilder.Draw += windows.Draw;
        plugin.UiBuilder.OpenMainUi += Open;
        commands.AddHandler("/aetherloom", new CommandInfo((_, _) => Open())
        {
            HelpMessage = "Inspect the ownership of equipment in a Glamourer design."
        });
    }

    private void Open() => window.IsOpen = true;

    public void Dispose()
    {
        commands.RemoveHandler("/aetherloom");
        plugin.UiBuilder.OpenMainUi -= Open;
        plugin.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
        ownership.Dispose();
    }
}

internal sealed class WardrobeWindow(IDalamudPluginInterface plugin, IDataManager data, WardrobeOwnership ownership)
    : Window("Aetherloom")
{
    private Dictionary<Guid, string> designs = [];
    private Guid selected;
    private JObject? equipment;
    private string? error;

    public override void OnOpen() => Refresh();

    private void Refresh()
    {
        try
        {
            designs = plugin.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2").InvokeFunc();
            if (!designs.ContainsKey(selected))
                selected = designs.OrderBy(d => d.Value).Select(d => d.Key).FirstOrDefault();
            ReadDesign();
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            designs.Clear();
            equipment = null;
            error = "Enable Glamourer, then refresh designs.";
        }
    }

    private void ReadDesign()
    {
        try
        {
            equipment = selected == Guid.Empty ? null : plugin.GetIpcSubscriber<Guid, JObject?>("Glamourer.GetDesignJObject")
                .InvokeFunc(selected)?["Equipment"] as JObject;
            error = equipment == null && selected != Guid.Empty ? "Design unavailable. Refresh designs." : null;
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            equipment = null;
            error = "Glamourer is unavailable. Refresh designs.";
        }
    }

    public override void Draw()
    {
        if (ImGui.Button("Refresh designs")) Refresh();
        if (designs.Count == 0)
        {
            ImGui.TextUnformatted(error ?? "No saved designs in Glamourer. Save a design there, then refresh.");
            return;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted($"{designs.Count} saved {(designs.Count == 1 ? "design" : "designs")}");
        if (ImGui.BeginCombo("Design", designs.GetValueOrDefault(selected) ?? "Select design"))
        {
            foreach (var (id, name) in designs.OrderBy(d => d.Value))
            {
                if (!ImGui.Selectable($"{name}##{id}", id == selected)) continue;
                selected = id;
                ReadDesign();
            }
            ImGui.EndCombo();
        }
        if (error != null) { ImGui.TextUnformatted(error); return; }
        if (ownership.Character == 0) { ImGui.TextUnformatted("Log in to check this character’s equipment."); return; }
        if (equipment == null || !ImGui.BeginTable("Equipment", 3)) return;
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Availability");
        ImGui.TableHeadersRow();
        foreach (var slot in equipment.Properties())
        {
            if (slot.Value is not JObject item || item["ItemId"]?.Type != JTokenType.Integer) continue;
            var id = item.Value<ulong>("ItemId");
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(slot.Name);
            ImGui.TableNextColumn();
            if (id > uint.MaxValue || !data.GetExcelSheet<Item>().TryGetRow((uint)id, out var row) || id == 0)
            {
                ImGui.TextUnformatted("Empty or custom slot");
                continue;
            }
            ImGui.TextUnformatted(row.Name.ExtractText());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(ownership.Availability((uint)id));
        }
        ImGui.EndTable();
    }
}
