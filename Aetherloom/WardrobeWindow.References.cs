using System.IO;
using Lumina.Excel.Sheets;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    internal const string DesignItemEndpoint = "Aetherloom.GetDesignItemIds";
    private uint[]? referenceIds;

    internal uint[] GetDesignItemIds()
    {
        if(!LocalReady)Refresh();
        if(!LocalReady)throw new InvalidDataException("Wardrobe migration is pending. "+Message);
        if(referenceIds==null)
        {
            var items=data.GetExcelSheet<Item>();
            referenceIds=WardrobeStore.ItemIds(Plans,Gear.Concat(Accessories),
                id=>items.TryGetRow(id,out var row) && row.EquipSlotCategory.RowId!=0);
        }
        return [..referenceIds];
    }
}
