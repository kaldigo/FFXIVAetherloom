using System.IO;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Cabinet = Lumina.Excel.Sheets.Cabinet;

namespace Aetherloom;

public sealed class WardrobeOwnership : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IPlayerState _player;
    private readonly IClientState _client;
    private readonly IGameGui _gui;
    private readonly IAddonLifecycle _addons;
    private readonly string _directory;
    private readonly (uint Row, uint Item)[] _cabinet;
    private readonly Dictionary<uint, uint[]> _outfits;
    private readonly HashSet<uint> _exceptions;
    private OwnershipSnapshot _snapshot = new();
    private ulong _character;
    private long _nextScan;
    private bool _dirty;

    public WardrobeOwnership(IFramework framework, IPlayerState player, IClientState client, IDalamudPluginInterface pi, IDataManager data, IPluginLog log, IGameGui gui, IAddonLifecycle addons)
    {
        _log = log;
        _player = player;
        _client = client;
        _gui = gui;
        _addons = addons;
        _framework = framework;
        _directory = pi.GetPluginConfigDirectory();
        _cabinet = data.GetExcelSheet<Cabinet>().Where(c => c.Item.RowId != 0).Select(c => (c.RowId, c.Item.RowId)).ToArray();
        _outfits = data.GetExcelSheet<MirageStoreSetItem>().ToDictionary(r => r.RowId, r => new[]
        {
            r.MainHand.RowId, r.OffHand.RowId, r.Head.RowId, r.Body.RowId, r.Hands.RowId, r.Legs.RowId,
            r.Feet.RowId, r.Earrings.RowId, r.Necklace.RowId, r.Bracelets.RowId, r.Ring.RowId,
        });
        using var catalog = typeof(WardrobeOwnership).Assembly.GetManifestResourceStream("Aetherloom.WardrobeExceptions.json")!;
        _exceptions = JsonSerializer.Deserialize<HashSet<uint>>(catalog)!;
        addons.RegisterListener(AddonEvent.PreClose, ["Cabinet", "CabinetWithdraw"], OnArmoireClosing);
        framework.Update += Update;
    }

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] Armoury = [InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings];
    private static readonly InventoryType[] Equipped = [InventoryType.EquippedItems];

    public ulong Character => _client.IsLoggedIn && _player.ContentId == _character ? _character : 0;

    internal object Snapshot=>_snapshot;
    public string Availability(uint itemId) => _snapshot.Availability(itemId, _exceptions.Contains(itemId), Character != 0);

    private static unsafe HashSet<uint>? ReadContainers(InventoryType[] types)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return null;
        var items = new HashSet<uint>();
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Size <= 0) return null;
            for (var i = 0; i < container->Size; ++i)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) return null;
                if (item->ItemId != 0 && item->Quantity > 0)
                    items.Add(ItemUtil.GetBaseId(item->ItemId).ItemId);
            }
        }
        return items;
    }

    private string CacheFile => Path.Combine(_directory, $"wardrobe-{_character:X16}.json");

    private unsafe void Update(IFramework framework)
    {
        var character = _client.IsLoggedIn ? _player.ContentId : 0;
        if (character != _character)
        {
            Save();
            _character = character;
            _dirty = false;
            _snapshot = new();
            _nextScan = 0;
            try
            {
                if (character != 0)
                {
                    // The earlier Glamourer fork used the same two storage fields and character key.
                    var previousFile = Path.Combine(_directory, "..", "Glamourer", Path.GetFileName(CacheFile));
                    var source = File.Exists(CacheFile) ? CacheFile : previousFile;
                    if (File.Exists(source))
                    {
                        _snapshot = JsonSerializer.Deserialize<OwnershipSnapshot>(File.ReadAllText(source)) ?? new();
                        _dirty = source == previousFile;
                    }
                }
            }
            catch (Exception e)
            {
                _log.Error($"Could not load wardrobe cache: {e}");
            }
        }

        var now = Environment.TickCount64;
        if (character == 0 || now < _nextScan)
            return;
        _nextScan = now + 500;

        var bags = ReadContainers(Bags);
        var armoury = ReadContainers(Armoury);
        var equipped = ReadContainers(Equipped);
        if (bags != null && _snapshot.Bags?.SetEquals(bags) != true)
        {
            _snapshot = _snapshot with { Bags = bags };
            _dirty = true;
        }
        if (armoury != null && _snapshot.Armoury?.SetEquals(armoury) != true)
        {
            _snapshot = _snapshot with { Armoury = armoury };
            _dirty = true;
        }
        if (equipped != null && _snapshot.Equipped?.SetEquals(equipped) != true)
        {
            _snapshot = _snapshot with { Equipped = equipped };
            _dirty = true;
        }

        var manager = MirageManager.Instance();
        if (manager != null && manager->PrismBoxLoaded)
        {
            var contents = new HashSet<uint>();
            var slots = manager->PrismBoxItemIds;
            for (var i = 0; i < slots.Length; ++i)
            {
                var id = slots[i] % 1_000_000;
                if (id == 0)
                    continue;
                if (!_outfits.TryGetValue(id, out var pieces))
                    contents.Add(id);
                else
                    for (var p = 0; p < pieces.Length; ++p)
                        if (pieces[p] != 0 && manager->IsSetSlotUnlocked((uint)i, p))
                            contents.Add(pieces[p]);
            }

            if (_snapshot.Dresser?.SetEquals(contents) != true)
            {
                _snapshot = _snapshot with { Dresser = contents };
                _dirty = true;
            }
        }

        RefreshArmoire(ArmoireWindowReady("Cabinet") || ArmoireWindowReady("CabinetWithdraw"));
        Save();
    }

    private unsafe bool ArmoireWindowReady(string name)
    {
        var addon = _gui.GetAddonByName(name);
        return !addon.IsNull && addon.IsVisible && ((AtkUnitBase*)addon.Address)->IsReady;
    }

    private void OnArmoireClosing(AddonEvent type, AddonArgs args)
    {
        if (Character == 0) return;
        RefreshArmoire(ArmoireWindowReady(args.AddonName));
        Save();
    }

    private void RefreshArmoire(bool windowReady)
    {
        var next = _snapshot.RefreshArmoire(windowReady, ReadLoadedArmoire);
        if (ReferenceEquals(next, _snapshot)) return;
        _log.Debug($"Updated armoire cache from the open armoire ({next.Armoire!.Count} items).");
        _snapshot = next;
        _dirty = true;
    }

    private unsafe HashSet<uint>? ReadLoadedArmoire()
    {
        var ui = UIState.Instance();
        if (ui == null || ui->Cabinet.State != FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet.CabinetState.Loaded)
            return null;
        var contents = new HashSet<uint>();
        foreach (var (row, item) in _cabinet)
            if (ui->Cabinet.IsItemInCabinet(row))
                contents.Add(item);
        return contents;
    }

    private void Save()
    {
        if (!_dirty)
            return;
        try
        {
            File.WriteAllText(CacheFile + ".tmp", JsonSerializer.Serialize(_snapshot));
            File.Move(CacheFile + ".tmp", CacheFile, true);
            _dirty = false;
        }
        catch (Exception e)
        {
            _log.Error($"Could not save wardrobe cache: {e}");
        }
    }

    public void Dispose()
    {
        _addons.UnregisterListener(OnArmoireClosing);
        _framework.Update -= Update;
        Save();
    }
}
