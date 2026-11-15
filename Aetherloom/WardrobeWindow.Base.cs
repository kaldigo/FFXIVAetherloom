using Newtonsoft.Json.Linq;
using Dalamud.Plugin.Ipc.Exceptions;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    private const string BaseTag="aetherloom-base";
    private readonly HashSet<Guid> favourites=[];
    private Dictionary<Guid,LibraryDesign> bases=[];
    private ulong previewCharacter;
    private string? projectionError;
    public IEnumerable<LibraryDesign> Bases=>bases.Values;
    public Guid? LinkedBase=>Guid.TryParse(design.Value<string>("BaseDesign"),out var id)?id:null;
    public void SetBase(Guid? id)
    {
        if(Selected==Guid.Empty || (id.HasValue && !bases.ContainsKey(id.Value)))return;
        if(id.HasValue)design["BaseDesign"]=id.ToString();else design.Remove("BaseDesign");
        Save();
    }
    public bool IsFavourite(Guid id)=>favourites.Contains(id);
    public void ToggleFavouriteDesign()
    {
        if(!library.ContainsKey(Selected))return;
        if(!favourites.Add(Selected))favourites.Remove(Selected);
        Write();
    }
    private bool ReadBase(JObject plan,out JObject? look,out string error)
    {
        look=null;error="";
        if(plan["BaseDesign"]==null)return true;
        if(!Guid.TryParse(plan.Value<string>("BaseDesign"),out var id) || !bases.ContainsKey(id))
        {error="Linked base unavailable. Choose a Glamourer design tagged aetherloom-base or clear the link.";return false;}
        var source=plugin.GetIpcSubscriber<Guid,JObject?>("Glamourer.GetDesignJObject").InvokeFunc(id);
        if(source?["Customize"] is not JObject appearance || appearance.Value<uint>("ModelId")!=0)
        {error="The linked base must be a player-character design.";return false;}
        look=appearance;return true;
    }
    internal Dictionary<Guid,string> ListDesigns(bool onlyFavourites)
    {
        Refresh();
        return disconnected?[]:library.Values.Where(d=>!onlyFavourites || IsFavourite(d.Id)).ToDictionary(d=>d.Id,d=>d.Name);
    }
    internal bool ApplyById(Guid id)
    {
        Refresh();
        if(disconnected || !library.ContainsKey(id)){Message="The wardrobe design is unavailable.";return false;}
        bool applied=ApplyDesignToCharacter(id,out var result,true);
        if(applied)AssignmentMode=false;
        Message=result;
        return applied;
    }
    public bool RevertAll()
    {
        if(!HasCharacter || objects.LocalPlayer is not {} player){Message="Your character is unavailable.";return false;}
        try
        {
            // GetState also initializes Glamourer's state on a character with no previous edits.
            var (read,_)=plugin.GetIpcSubscriber<int,uint,(int,JObject?)>("Glamourer.GetState").InvokeFunc(player.ObjectIndex,0);
            if(read!=0){Message=$"Glamourer could not read your character (code {read}).";return false;}
            var code=plugin.GetIpcSubscriber<int,uint,ulong,int>("Glamourer.RevertState").InvokeFunc(player.ObjectIndex,0,6);
            if(code is not (0 or 1)){Message=$"Glamourer could not revert your character (code {code}).";return false;}
            CharacterSettings.Remove("AppliedFields");
            AssignmentMode=false;projection=null;Write();
            return true;
        }
        catch(IpcError){Message="Glamourer is unavailable.";return false;}
    }
}
