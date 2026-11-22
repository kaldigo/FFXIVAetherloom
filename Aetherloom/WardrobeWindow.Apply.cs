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
        _=ApplyById(Selected);
    }

    internal bool ApplyDesignToCharacter(Guid id,out string result,bool manual=false)
    {
        result="Your character is unavailable.";
        if(objects.LocalPlayer is not {} player || !HasCharacter)return false;
        try
        {
            var plan=id==Guid.Empty ? new JObject{["Equipment"]=new JObject(),["Customize"]=new JObject()} : Plans[id.ToString()] as JObject;
            if(plan?["Equipment"] is not JObject || plan["Customize"] is not JObject)
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
            if(!ReadBase(plan,out var baseLook,out result))return false;
            var composed=EditablePlan(plan);
            var (key,main)=WardrobeWeapons.Hands(composed).FirstOrDefault(h=>Jobs(Resolve(h.Slot.Value<ulong>("ItemId")).Jobs).Contains(job));
            var gear=(JObject)composed["Equipment"]!;
            gear.Remove("OffHand");
            if(main!=null && PairedOff(composed,key,main) is {} off)gear["OffHand"]=off;
            var previous=CharacterSettings["AppliedFields"] as JObject??new JObject();
            var look=WardrobeBase.Compose(baseLook,(JObject)plan["Customize"]!);
            composed["Customize"]=WardrobeBase.WithLipColor(look,plan["LipColorOverride"] as JObject,
                currentLook["Race"]?.Value<byte>("Value")??0,previous);
            var fields=WardrobeBase.Appearance.Select(f=>f.Key).ToArray();
            var skipped=new List<string>();
            var payload=WardrobeApplication.BuildPlan(currentGear,currentLook,composed,Gear.Concat(Accessories),
                fields,main,CanApplyItem,skipped,out bool applyLook);
            if(!WardrobeApplication.PlanRestoration(previous,payload,currentGear,currentLook,manual,Equipped,fields,out var next,out var restoreLook,baseLook))
            {result="Waiting for equipped gear.";return false;}
            applyLook|=restoreLook;
            // Include Customization: Glamourer's RemoveCustomize also clears equipment display flags.
            // BuildPlan clears Apply on every appearance field the design does not explicitly change.
            var response=plugin.GetIpcSubscriber<object,int,uint,ulong,int>("Glamourer.ApplyState")
                .InvokeFunc(payload,player.ObjectIndex,0,7UL);
            if(response!=0)
            {
                result=$"Glamourer could not apply the design (code {response}).";
                return false;
            }
            if(applyLook)
            {
                try
                {
                    var (code,applied)=plugin.GetIpcSubscriber<int,uint,(int,JObject?)>("Glamourer.GetState").InvokeFunc(player.ObjectIndex,0);
                    if(code==0 && applied?["Customize"] is JObject actual)
                        WardrobeBase.RecordApplied(previous,next,currentLook,actual,baseLook,(JObject)payload["Customize"]!);
                }
                catch(Dalamud.Plugin.Ipc.Exceptions.IpcError) { /* Keep the requested values if readback is unavailable. */ }
            }
            if(!JToken.DeepEquals(previous,next)){CharacterSettings["AppliedFields"]=next;projection=null;Write();}
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
        if(WardrobeWeapons.IsEmptyItem(id))return true;
        return id<=uint.MaxValue && ownership.Availability((uint)id) is not ("Missing" or "Unconfirmed");
    }
}
