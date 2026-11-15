using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Aetherloom;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface plugin;
    private readonly ICommandManager commands;
    private readonly WardrobeOwnership ownership;
    private readonly WindowSystem windows = new("Aetherloom");
    private readonly CharacterPreview preview;
    private readonly WardrobeWindow window;
    private readonly WardrobeAssignments assignments;

    public Plugin(IDalamudPluginInterface plugin, ICommandManager commands, IFramework framework,
        IPlayerState player, IClientState client, IDataManager data, IPluginLog log,
        IGameGui gameGui, ICondition conditions, IObjectTable objects, IAddonLifecycle addons, ITextureProvider textures)
    {
        this.plugin = plugin;
        this.commands = commands;
        ownership = new(framework, player, client, plugin, data, log, gameGui, addons);
        preview = new(framework, gameGui, conditions, objects, addons, log, data, textures);
        window = new(plugin, data, ownership, preview, textures, objects);
        plugin.GetIpcProvider<uint[]>(WardrobeWindow.DesignItemEndpoint).RegisterFunc(window.GetDesignItemIds);
        plugin.GetIpcProvider<bool>("Aetherloom.GetAssignmentMode").RegisterFunc(()=>window.AssignmentMode);
        plugin.GetIpcProvider<bool,bool>("Aetherloom.SetAssignmentMode").RegisterFunc(mode=>window.AssignmentMode=mode);
        plugin.GetIpcProvider<bool>("Aetherloom.ToggleAssignmentMode").RegisterFunc(()=>window.AssignmentMode=!window.AssignmentMode);
        plugin.GetIpcProvider<Guid,bool>("Aetherloom.ApplyDesign").RegisterFunc(window.ApplyById);
        plugin.GetIpcProvider<Dictionary<Guid,string>>("Aetherloom.GetDesigns").RegisterFunc(()=>window.ListDesigns(false));
        plugin.GetIpcProvider<Dictionary<Guid,string>>("Aetherloom.GetFavouriteDesigns").RegisterFunc(()=>window.ListDesigns(true));
        plugin.GetIpcProvider<bool>("Aetherloom.RevertAll").RegisterFunc(window.RevertAll);
        assignments=new(window,framework,client,objects,conditions,log,plugin);
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
        plugin.GetIpcProvider<uint[]>(WardrobeWindow.DesignItemEndpoint).UnregisterFunc();
        plugin.GetIpcProvider<bool>("Aetherloom.GetAssignmentMode").UnregisterFunc();
        plugin.GetIpcProvider<bool,bool>("Aetherloom.SetAssignmentMode").UnregisterFunc();
        plugin.GetIpcProvider<bool>("Aetherloom.ToggleAssignmentMode").UnregisterFunc();
        plugin.GetIpcProvider<Guid,bool>("Aetherloom.ApplyDesign").UnregisterFunc();
        plugin.GetIpcProvider<Dictionary<Guid,string>>("Aetherloom.GetDesigns").UnregisterFunc();
        plugin.GetIpcProvider<Dictionary<Guid,string>>("Aetherloom.GetFavouriteDesigns").UnregisterFunc();
        plugin.GetIpcProvider<bool>("Aetherloom.RevertAll").UnregisterFunc();
        commands.RemoveHandler("/aetherloom");
        plugin.UiBuilder.OpenMainUi -= Open;
        plugin.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
        assignments.Dispose();
        window.DisposeImports();
        preview.Dispose();
        ownership.Dispose();
    }
}
