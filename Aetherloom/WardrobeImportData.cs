using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Aetherloom;

internal static class WardrobeImportData
{
    internal static bool Fits(EquipSlotCategory e,string key)=>key switch{
        "MainHand"=>e.MainHand>0,"OffHand"=>e.OffHand>0,"Head"=>e.Head>0,"Body"=>e.Body>0,"Hands"=>e.Gloves>0,
        "Legs"=>e.Legs>0,"Feet"=>e.Feet>0,"Ears"=>e.Ears>0,"Neck"=>e.Neck>0,"Wrists"=>e.Wrists>0,"RFinger"=>e.FingerR>0,"LFinger"=>e.FingerL>0,_=>false};

    internal static WardrobeImport Resolve(uint id,string json,ExcelSheet<Item> items,ExcelSheet<Stain> dyes)
    {
        var names = items.Where(i=>i.EquipSlotCategory.RowId!=0)
            .ToLookup(i=>WardrobeImport.Normalize(i.Name.ExtractText()), StringComparer.OrdinalIgnoreCase);
        var stains = dyes.Where(s=>s.RowId is >0 and <256)
            .ToLookup(s=>s.Name.ExtractText().Replace('-',' '),StringComparer.OrdinalIgnoreCase);
        return WardrobeImport.Collection(id,json,
            (slot,name)=>names[name].Where(i=>Fits(i.EquipSlotCategory.Value,slot)).Select(i=>(uint?)i.RowId).FirstOrDefault(),
            name=>stains[name].Select(s=>(byte?)s.RowId).FirstOrDefault());
    }
}
