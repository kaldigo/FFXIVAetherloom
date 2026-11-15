using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    public bool BypassOwnership
    {
        get => design.Value<bool>("BypassOwnership");
        set { design["BypassOwnership"]=value; Save(); }
    }

    public void ApplyToCharacter()
    {
        if(Selected==Guid.Empty || objects.LocalPlayer is not {} player || !HasCharacter)return;
        var skipped=new List<string>();
        var applied=Call(()=>
        {
            var (read,state)=plugin.GetIpcSubscriber<int,uint,(int,JObject?)>("Glamourer.GetState").InvokeFunc(player.ObjectIndex,0);
            if(read!=0 || state?["Customize"] is not JObject currentLook || state["Equipment"] is not JObject currentGear)
                return "Could not read your current Glamourer appearance.";
            if(currentLook.Value<uint>("ModelId")!=0)return "Character application requires a humanoid appearance.";
            var job=data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English).GetRow(player.ClassJob.RowId).Abbreviation.ExtractText();
            var main=Slots(false).FirstOrDefault(s=>s.Key.StartsWith("MainHand") && Jobs(s.Item.Jobs).Contains(job));
            var payload=WardrobeApplication.Build(currentGear,currentLook,Equipment,Look,Gear.Concat(Accessories),
                Fields("Hair").Concat(Fields("Face paint")),main==null?null:Entry(main.Key),
                id=>BypassOwnership || CanApplyItem(id),skipped,out bool applyLook);
            // Once + Equipment, plus Customization when a wardrobe appearance field is enabled.
            var response=plugin.GetIpcSubscriber<object,int,uint,ulong,int>("Glamourer.ApplyState")
                .InvokeFunc(payload,player.ObjectIndex,0,applyLook?7UL:3UL);
            return response==0?"":$"Glamourer could not apply the design (code {response}).";
        });
        if(applied)Message=skipped.Count==0?"Design applied.":"Skipped for ownership: "+string.Join(", ",skipped.Select(key=>Labels.GetValueOrDefault(key,key)))+".";
    }

    private bool CanApplyItem(ulong id)
    {
        // Zero and Glamourer's slot-specific Nothing IDs represent an explicitly empty slot.
        if(id==0 || id is <= uint.MaxValue-128 and >= uint.MaxValue-150
            || id is <= uint.MaxValue-384 and >= uint.MaxValue-639)return true;
        return id<=uint.MaxValue && ownership.Availability((uint)id) is not ("Missing" or "Unconfirmed");
    }
}
