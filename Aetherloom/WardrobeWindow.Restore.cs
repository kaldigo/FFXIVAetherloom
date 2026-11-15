using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    internal bool HasAppliedFields=>CharacterSettings["AppliedFields"] is JObject {Count:>0};
    private static unsafe JObject? Equipped(int index)
    {
        var manager=InventoryManager.Instance();if(manager==null)return null;
        var bag=manager->GetInventoryContainer(InventoryType.EquippedItems);
        if(bag==null || !bag->IsLoaded || bag->Size<=index)return null;
        var item=bag->GetInventorySlot(index);if(item==null)return null;
        return new JObject{["ItemId"]=item->GlamourId!=0?item->GlamourId:item->ItemId,["Stain"]=item->Stains[0],["Stain2"]=item->Stains[1]};
    }
}
