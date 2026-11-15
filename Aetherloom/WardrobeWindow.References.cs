using System.IO;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    internal const string DesignItemEndpoint = "Aetherloom.GetDesignItemIds";
    private string? referenceSnapshot;
    private uint[] referenceIds = [];

    internal uint[] GetDesignItemIds()
    {
        // Read the library directly: the wardrobe window may never have been opened.
        var snapshot = plugin.GetIpcSubscriber<string>(Api+"DesignLibrary").InvokeFunc();
        if (snapshot != referenceSnapshot || snapshot == null)
        {
            var designs = JObject.Parse(snapshot ?? throw new InvalidDataException("Glamourer library unavailable."))
                ["Designs"]?.ToObject<LibraryDesign[]>() ?? throw new InvalidDataException("Missing Glamourer designs.");
            var items = data.GetExcelSheet<Item>();
            var ids = new HashSet<uint>();
            foreach (var entry in designs.Where(d => WardrobeLibrary.Tagged(d.Tags,WardrobeLibrary.Tag)))
            {
                var plan = Plans[entry.Id.ToString()] as JObject ??
                    plugin.GetIpcSubscriber<Guid,JObject?>("Glamourer.GetDesignJObject").InvokeFunc(entry.Id)
                    ?? throw new InvalidDataException($"Design {entry.Id} is unavailable.");
                var equipment = plan["Equipment"] as JObject ?? throw new InvalidDataException("Missing design equipment.");
                foreach (var key in Gear.Concat(Accessories)) Add(equipment[key]);
                if (plan["AdditionalMainHands"] is JObject hands)
                    foreach (var hand in hands.Properties()) Add(hand.Value);
            }
            referenceIds = ids.Order().ToArray();
            referenceSnapshot = snapshot;

            void Add(JToken? slot)
            {
                if (slot?.Value<ulong?>("ItemId") is > 0 and <= uint.MaxValue and var id
                    && items.TryGetRow((uint)id,out var row) && row.EquipSlotCategory.RowId != 0)
                    ids.Add((uint)id);
            }
        }
        return [..referenceIds];
    }
}
