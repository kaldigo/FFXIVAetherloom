using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Aetherloom;

internal sealed class WardrobeAssignments : IDisposable
{
    private readonly WardrobeWindow window;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface plugin;
    private WardrobeAssignment? observed, applied;
    private long nextCheck;
    private nint finalizedActor;
    private string? lastError;

    // Glamourer's StateFinalized provider registers this label in the current API.
    private const string Finalized="Penumbra.StateFinalized";

    public WardrobeAssignments(WardrobeWindow window,IFramework framework,IClientState client,IObjectTable objects,
        ICondition condition,IPluginLog log,IDalamudPluginInterface plugin)
    {
        (this.window,this.framework,this.client,this.objects,this.condition,this.log,this.plugin)=(window,framework,client,objects,condition,log,plugin);
        plugin.GetIpcSubscriber<nint,int,object?>(Finalized).Subscribe(OnFinalized);
        framework.Update+=Update;
    }

    private void OnFinalized(nint actor,int reason)
    {
        // Revert to automation, reapply automation, or gearset loaded. Ignore our own DesignApplied event.
        if(reason is 6 or 8 or 9 && actor!=0 && actor==objects.LocalPlayer?.Address)
            Interlocked.Exchange(ref finalizedActor,actor);
    }

    private void Update(IFramework _)
    {
        var now=Environment.TickCount64;
        if(now<nextCheck)return;
        nextCheck=now+300;
        try
        {
            var actor=objects.LocalPlayer;
            var finalized=Interlocked.Exchange(ref finalizedActor,0);
            var target=actor==null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
                ? null : window.GetAssignment(actor.ClassJob.RowId,client.TerritoryType);
            if(target!=observed || (target!=null && finalized!=0 && finalized==actor?.Address))
            {
                // Let the new actor/gearset settle, then resolve again before applying.
                observed=target;
                applied=null;
                lastError=null;
                return;
            }
            if(target is not {} assignment || applied==target)return;
            if(window.ApplyDesignToCharacter(assignment.Design,out var result))
            {
                applied=assignment;
                lastError=null;
                log.Debug($"Applied wardrobe assignment {assignment.Design} (job {assignment.Job}, zone {assignment.Zone}). {result}");
            }
            else Retry(result);
        }
        catch(Exception ex){ Retry(ex.Message); }

        void Retry(string error)
        {
            nextCheck=now+2500;
            if(lastError!=error)log.Warning($"Wardrobe assignment is waiting: {error}");
            lastError=error;
        }
    }

    public void Dispose()
    {
        framework.Update-=Update;
        plugin.GetIpcSubscriber<nint,int,object?>(Finalized).Unsubscribe(OnFinalized);
    }
}
