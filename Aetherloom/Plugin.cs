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

    public Plugin(IDalamudPluginInterface plugin, ICommandManager commands, IFramework framework,
        IPlayerState player, IClientState client, IDataManager data, IPluginLog log,
        IGameGui gameGui, ICondition conditions, IObjectTable objects, IAddonLifecycle addons, ITextureProvider textures)
    {
        this.plugin = plugin;
        this.commands = commands;
        ownership = new(framework, player, client, plugin, data, log);
        preview = new(framework, gameGui, conditions, objects, addons, log, data, textures);
        window = new(plugin, data, ownership, preview, textures, objects);
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
        preview.Dispose();
        ownership.Dispose();
    }
}
