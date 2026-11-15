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
        ApplyDesignToCharacter(Selected,out var result);
        Message=result;
    }

    internal WardrobeAssignment? GetAssignment(uint job,uint zone)
    {
        var character=ownership.Character;
        if(character==0 || assignments[character.ToString()] is not JObject bindings
            || (bindings["Jobs"]?[job.ToString()]==null && bindings["Zones"]?[zone.ToString()]==null))return null;
        if(Environment.TickCount64>=nextRefresh)Refresh();
        return disconnected ? null : WardrobeAssignment.Resolve(bindings,character,job,zone,library.ContainsKey);
    }

    internal bool ApplyDesignToCharacter(Guid id,out string result)
    {
        result="Your character is unavailable.";
        if(id==Guid.Empty || objects.LocalPlayer is not {} player || !HasCharacter)return false;
        try
        {
            var plan=Plans[id.ToString()] as JObject
                ?? plugin.GetIpcSubscriber<Guid,JObject?>("Glamourer.GetDesignJObject").InvokeFunc(id);
            if(plan?["Equipment"] is not JObject equipment || plan["Customize"] is not JObject)
            {
                result="Could not read the wardrobe design.";
                return false;
            }
            var (read,state)=plugin.GetIpcSubscriber<int,uint,(int,JObject?)>("Glamourer.GetState").InvokeFunc(player.ObjectIndex,0);
            if(read!=0 || state?["Customize"] is not JObject currentLook || state["Equipment"] is not JObject currentGear)
            {
                result="Could not read your current Glamourer appearance.";
                return false;
            }
            if(currentLook.Value<uint>("ModelId")!=0)
            {
                result="Character application requires a humanoid appearance.";
                return false;
            }
            var job=data.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English).GetRow(player.ClassJob.RowId).Abbreviation.ExtractText();
            var hands=plan["AdditionalMainHands"] is JObject extras ? extras.Properties().Select(p=>p.Value) : [];
            var main=hands.Prepend(equipment["MainHand"]).OfType<JObject>().FirstOrDefault(slot=>
                Jobs(Resolve(slot.Value<ulong>("ItemId")).Jobs).Contains(job));
            var skipped=new List<string>();
            var payload=WardrobeApplication.BuildPlan(currentGear,currentLook,plan,Gear.Concat(Accessories),
                Fields("Hair").Concat(Fields("Face paint")),main,CanApplyItem,skipped,out bool applyLook);
            // Once + Equipment, plus Customization when a wardrobe appearance field is enabled.
            var response=plugin.GetIpcSubscriber<object,int,uint,ulong,int>("Glamourer.ApplyState")
                .InvokeFunc(payload,player.ObjectIndex,0,applyLook?7UL:3UL);
            if(response!=0)
            {
                result=$"Glamourer could not apply the design (code {response}).";
                return false;
            }
            result=skipped.Count==0?"Design applied.":"Skipped for ownership: "+string.Join(", ",skipped.Select(key=>Labels.GetValueOrDefault(key,key)))+".";
            return true;
        }
        catch(Dalamud.Plugin.Ipc.Exceptions.IpcError)
        {
            result="Glamourer is unavailable.";
            return false;
        }
    }

    private bool CanApplyItem(ulong id)
    {
        // Zero and Glamourer's slot-specific Nothing IDs represent an explicitly empty slot.
        if(id==0 || id is <= uint.MaxValue-128 and >= uint.MaxValue-150
            || id is <= uint.MaxValue-384 and >= uint.MaxValue-639)return true;
        return id<=uint.MaxValue && ownership.Availability((uint)id) is not ("Missing" or "Unconfirmed");
    }
}
