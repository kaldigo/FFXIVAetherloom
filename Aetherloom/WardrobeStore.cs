using System.IO;
using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeStore
{
    internal static Dictionary<Guid,string> ListSavedDesigns(string glamourerDirectory)
    {
        var result=new Dictionary<Guid,string>();
        try
        {
            foreach(var file in Directory.EnumerateFiles(Path.Combine(glamourerDirectory,"designs"),"*.json"))
            {
                var name=Path.GetFileNameWithoutExtension(file);
                if(Guid.TryParseExact(name,"D",out var id))result[id]=name;
            }
        }
        catch(DirectoryNotFoundException){ /* Fresh profile with no Glamourer designs. */ }
        return result;
    }

    internal static LibraryDesign Describe(Guid id,string name,JObject source)
    {
        if(!Guid.TryParse(source.Value<string>("Identifier"),out var identifier) || identifier!=id)
            throw new InvalidDataException($"Glamourer returned incomplete design metadata for '{name}'.");
        return new(id,source.Value<string>("Name")??name,source["Tags"]?.ToObject<string[]>()??[],
            source.Value<string>("FileSystemFolder")??"");
    }

    internal static bool IsAppearanceBase(LibraryDesign entry)
        =>WardrobeLibrary.Tagged(entry.Tags,"aetherloom-base") && !WardrobeLibrary.Tagged(entry.Tags,WardrobeLibrary.Tag);

    internal static JObject MigrateFromDesigns(JObject original,Dictionary<Guid,string> listed,string[] folders,Func<Guid,JObject?> read)
    {
        if(listed.Count==0 && original["Plans"] is JObject { Count: > 0 })throw new InvalidDataException("Waiting for the Glamourer library.");
        var entries=new List<LibraryDesign>();
        var sources=new Dictionary<Guid,JObject>();
        foreach(var (id,name) in listed)
        {
            var source=read(id)??throw new InvalidDataException($"Could not read '{name}'.");
            var entry=Describe(id,name,source);
            entries.Add(entry);
            if(original["Plans"]?[id.ToString()]==null && entry.Tags.Any(t=>!WardrobeLibrary.VisibleTag(t)))sources[id]=source;
        }

        return Migrate(original,entries.ToArray(),folders,id=>sources.GetValueOrDefault(id));
    }

    internal static bool IsLocal(JObject document)=>document.Value<int?>("LibraryVersion")==1;

    internal static JObject Migrate(JObject original, LibraryDesign[] entries, string[] folders, Func<Guid,JObject?> fetch)
    {
        if(original["LibraryVersion"]!=null)
            throw new InvalidDataException("This wardrobe already has a local library version.");
        var document=(JObject)original.DeepClone();
        var local=document["Plans"] as JObject??new();
        var plans=new JObject();
        var paths=folders.Where(WardrobeLibrary.Managed).ToHashSet(StringComparer.Ordinal);
        foreach(var entry in entries)
        {
            bool tagged=WardrobeLibrary.Tagged(entry.Tags,WardrobeLibrary.Tag) || WardrobeLibrary.Tagged(entry.Tags,WardrobeLibrary.LegacyTag);
            var id=entry.Id.ToString();
            if(!tagged && !Pending(local[id]))continue;
            var source=local[id] as JObject??fetch(entry.Id);
            if(source?["Equipment"] is not JObject || source["Customize"] is not JObject)
                throw new InvalidDataException($"Could not read the complete wardrobe design '{entry.Name}'.");
            var plan=(JObject)source.DeepClone();
            var tags=Pending(local[id])?entry.Tags.Concat(plan["Tags"]!.Values<string>().OfType<string>()):entry.Tags;
            plan["Name"]=entry.Name;
            plan["Tags"]=new JArray(tags.Where(WardrobeLibrary.VisibleTag).Append(WardrobeLibrary.Tag).Distinct(StringComparer.OrdinalIgnoreCase));
            string folder=WardrobeLibrary.Managed(entry.Folder)?entry.Folder:"";
            plan["Folder"]=folder;
            for(var path=folder;path.Length>0;path=WardrobeLibrary.Parent(path))paths.Add(path);
            plan.Remove("LibraryLinked");
            plans.Add(id,plan);
        }
        foreach(var property in local.Properties().Where(p=>Pending(p.Value) && plans[p.Name]==null))
        {
            if(!Guid.TryParse(property.Name,out _) || property.Value["Equipment"] is not JObject || property.Value["Customize"] is not JObject)
                throw new InvalidDataException("Could not read a legacy wardrobe design.");
            var plan=(JObject)property.Value.DeepClone();
            plan["Folder"]="";
            plan["Tags"]=new JArray(plan["Tags"]!.Values<string>().OfType<string>().Where(WardrobeLibrary.VisibleTag).Append(WardrobeLibrary.Tag));
            plan.Remove("LibraryLinked");
            plans.Add(property.Name,plan);
        }
        document["Plans"]=plans;
        document["Folders"]=new JArray(paths.Order(StringComparer.Ordinal));
        document["LibraryVersion"]=1;
        return document;
    }

    private static bool Pending(JToken? plan)=>plan is JObject p && !p.Value<bool>("LibraryLinked")
        && p["Tags"] is JArray tags && tags.Values<string>().Contains(WardrobeLibrary.LegacyTag,StringComparer.OrdinalIgnoreCase);

    internal static void ChangeFolder(JObject document,string source,string parent,string? newName)
    {
        var paths=document["Folders"]!.Values<string>().OfType<string>().ToHashSet(StringComparer.Ordinal);
        if(!paths.Contains(source) || (parent!="" && !paths.Contains(parent)) || WardrobeLibrary.Within(parent,source))
            throw new InvalidDataException("Choose an existing destination outside the folder.");
        string Map(string path)
        {
            if(!WardrobeLibrary.Within(path,source))return path;
            if(newName!=null)return WardrobeLibrary.PathFor(parent,newName)+path[source.Length..];
            if(path==source)return parent;
            string tail=path[(source.Length+1)..];
            return WardrobeLibrary.PathFor(parent,tail);
        }
        var moved=paths.Where(p=>newName!=null || p!=source).Select(Map).ToArray();
        if(moved.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=moved.Length)
            throw new InvalidDataException("A folder with that name already exists in the destination.");
        document["Folders"]=new JArray(moved.Order(StringComparer.Ordinal));
        foreach(var plan in ((JObject)document["Plans"]!).Properties().Select(p=>(JObject)p.Value))
            plan["Folder"]=Map(plan.Value<string>("Folder")??"");
    }

    internal static uint[] ItemIds(JObject plans,IEnumerable<string> slots,Func<uint,bool> valid)
    {
        var ids=new HashSet<uint>();
        foreach(var p in plans.Properties())
        {
            var equipment=p.Value["Equipment"] as JObject??throw new InvalidDataException("Missing design equipment.");
            foreach(var slot in slots) Add(equipment[slot]);
            if(p.Value["PairedOffHands"] is JObject pairs)
                foreach(var pair in pairs.Properties())Add(pair.Value);
            if(p.Value["AdditionalMainHands"] is JObject hands)
                foreach(var hand in hands.Properties()) Add(hand.Value);
        }
        return ids.Order().ToArray();
        void Add(JToken? slot)
        {
            if(slot?.Value<ulong?>("ItemId") is >0 and <=uint.MaxValue and var id && valid((uint)id))ids.Add((uint)id);
        }
    }
}
