using System.IO;
using System.Net.Http;
using Dalamud.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal sealed partial class WardrobeWindow
{
    private readonly HttpClient importHttp = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2_000_000 };
    private Task<WardrobeImport>? loading;
    private WardrobeImport? importing;
    private string destination = "";
    private int folderIndex, plateIndex, created, skipped;
    private readonly HashSet<string> importFolders = new(StringComparer.OrdinalIgnoreCase);
    private Guid firstImported;
    public bool ImportBusy => loading != null || importing != null;
    public string ImportStatus { get; private set; } = "";

    public void StartImport(bool fromPlates, string url, string parent)
    {
        if (ImportBusy) return;
        try
        {
            if(!LocalReady){ImportStatus="Waiting for the wardrobe migration.";return;}
            destination = fromPlates ? WardrobeLibrary.PathFor("","Glamaholic") : parent;
            folderIndex = plateIndex = created = skipped = 0;
            firstImported = Guid.Empty;
            importFolders.Clear();
            importFolders.UnionWith(Folders);
            if (fromPlates)
            {
                var path = Path.Combine(plugin.ConfigFile.DirectoryName!,"Glamaholic.json");
                if (!File.Exists(path)) throw new FileNotFoundException("No saved Glamaholic library was found in this Dalamud profile.");
                loading = Task.Run(() => WardrobeImport.Glamaholic(File.ReadAllText(path)));
            }
            else
                loading = LoadCollection(WardrobeImport.CollectionId(url));
            ImportStatus = fromPlates ? "Reading Glamaholic library…" : "Loading Eorzea Collection outfit…";
        }
        catch (Exception ex) { ImportStatus = ex.Message; }
    }

    private async Task<WardrobeImport> LoadCollection(uint id)
    {
        var items=data.GetExcelSheet<Item>(ClientLanguage.English);
        var dyes=data.GetExcelSheet<Stain>(ClientLanguage.English);
        using var request = new HttpRequestMessage(HttpMethod.Get,$"https://ffxiv.eorzeacollection.com/api/glamour/{id}");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        using var response = await importHttp.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return WardrobeImportData.Resolve(id,json,items,dyes);
    }

    private void AdvanceImport()
    {
        try
        {
            if (loading is { IsCompleted:true } finished)
            {
                loading = null;
                importing = finished.GetAwaiter().GetResult();
                if (importing.Folders.Count+importing.Plates.Count>0 && !EnsureImportFolder(destination)) return;
            }
            if (importing is not {} batch) return;
            if (folderIndex < batch.Folders.Count)
            {
                EnsureImportFolder(destination+batch.Folders[folderIndex++]);
                return;
            }
            if (plateIndex < batch.Plates.Count)
            {
                var plate = batch.Plates[plateIndex++];
                ImportStatus = $"Importing {plateIndex} of {batch.Plates.Count} · {plate.Name}";
                if (library.ContainsKey(plate.Id))
                {
                    skipped++;
                    if(firstImported==Guid.Empty && library.ContainsKey(plate.Id)) firstImported=plate.Id;
                    return;
                }
                var tags = plate.Tags.Where(WardrobeLibrary.VisibleTag).Append(WardrobeLibrary.Tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var gear = new JObject();
                foreach(var (slot,item) in plate.Items)
                    gear[slot] = new JObject { ["ItemId"]=item.Id, ["Stain"]=item.First, ["Stain2"]=item.Second, ["Apply"]=true, ["ApplyStain"]=true };
                var plan=new JObject{["Equipment"]=gear,["Customize"]=new JObject(),["Tags"]=new JArray(tags)};
                if(!AddPlan(plate.Id,plan,plate.Name,destination+plate.Folder)){StopImport(Message);return;}
                created++;
                if(firstImported==Guid.Empty) firstImported=plate.Id;
                return;
            }
            importing = null;
            Refresh();
            if(library.ContainsKey(firstImported)) Select(firstImported);
            ImportStatus = $"Imported {created} design(s). {skipped} already imported.";
            if(batch.Warnings.Count>0) ImportStatus += " Unmatched: "+string.Join(", ",batch.Warnings)+".";
        }
        catch(Exception ex) { StopImport(ex.Message); }
    }

    private bool EnsureImportFolder(string path)
    {
        if(path.Length==0 || importFolders.Contains(path)) return true;
        var parent=WardrobeLibrary.Parent(path);
        var name=WardrobeLibrary.Label(path);
        if(!CreateFolder(parent,name))
        { StopImport(Message); return false; }
        importFolders.Add(path);
        return true;
    }

    private void StopImport(string reason)
    {
        loading=null;
        importing=null;
        ImportStatus=$"Import stopped after {created} new design(s). {reason}";
        lastSnapshot=null;
    }

    public void DisposeImports() => importHttp.Dispose();
}
