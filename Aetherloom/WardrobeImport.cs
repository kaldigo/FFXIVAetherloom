using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aetherloom;

internal sealed record ImportedItem(uint Id, byte First, byte Second);
internal sealed record ImportedPlate(Guid Id, string Name, string Folder, string[] Tags, Dictionary<string, ImportedItem> Items);
internal sealed class WardrobeImport
{
    public List<string> Folders { get; } = [];
    public List<ImportedPlate> Plates { get; } = [];
    public List<string> Warnings { get; } = [];

    private static readonly (string Key, string Plate, string Ec, uint Emperor)[] Slots =
    [
        ("MainHand","MainHand","weapon",0), ("OffHand","OffHand","offhand",0),
        ("Head","Head","head",10032), ("Body","Body","body",10033), ("Hands","Hands","hands",10034),
        ("Legs","Legs","legs",10035), ("Feet","Feet","feet",10036), ("Ears","Ears","earrings",9293),
        ("Neck","Neck","necklace",9292), ("Wrists","Wrists","bracelets",9294),
        ("RFinger","RightRing","right_ring",9295), ("LFinger","LeftRing","left_ring",9295)
    ];

    // Glamourer's filesystem interprets ASCII slashes as separators even inside display names.
    private static string Label(JsonNode? node, string fallback) =>
        node?.GetValue<string>().Trim().Replace('/', '／').Replace('\\', '＼') is { Length: > 0 } name ? name : fallback;

    private static Guid SourceId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("Aetherloom/import/"+key)).AsSpan(0,16));
    private static JsonArray Array(JsonNode? token) =>
        (token is JsonObject wrapper ? wrapper["$values"] : token) as JsonArray
        ?? throw new FormatException("Expected a list in the Glamaholic library.");

    public static WardrobeImport Glamaholic(string json)
    {
        var result = new WardrobeImport();
        Visit(Array(JsonNode.Parse(json)?["Plates"]), "", "");
        return result;

        void Visit(JsonArray nodes, string folder, string parentIdentity)
        {
            var occurrences = new Dictionary<string,int>();
            foreach (var node in nodes)
            {
                if (node is not JsonObject entry) throw new FormatException("Invalid Glamaholic library entry.");
                if ((string?)entry["NodeType"] == "Folder")
                {
                    var path = folder+"/"+Label(entry["Name"],"Folder");
                    result.Folders.Add(path);
                    Visit(Array(entry["Children"]),path,entry["Id"]?.ToString() ?? path);
                    continue;
                }
                if (entry["NodeType"] != null && (string?)entry["NodeType"] != "Plate")
                    throw new FormatException("Unknown Glamaholic library entry type.");
                var plate = entry["Plate"] as JsonObject ?? entry;
                var name = Label(plate["Name"],"Untitled");
                var source = plate["Items"] as JsonObject ?? throw new FormatException($"Missing items for {name}.");
                var items = new Dictionary<string,ImportedItem>();
                foreach (var (key, slot, _, emperor) in Slots)
                {
                    if (source[slot] is JsonObject item)
                        items[key] = new((uint)(item["ItemId"] ?? throw new FormatException($"Missing item ID in {name}.")),
                            (byte?)item["Stain1"] ?? (byte?)item["StainId"] ?? 0, (byte?)item["Stain2"] ?? 0);
                    else if (emperor != 0 && (bool?)plate["FillWithNewEmperor"] == true)
                        items[key] = new(emperor,0,0);
                }
                var count = occurrences.GetValueOrDefault(name);
                occurrences[name] = count+1;
                var identity = entry["Id"]?.ToString() ?? $"{parentIdentity}/{name}/{count}";
                string[] tags = plate["Tags"] == null ? [] : Array(plate["Tags"]).Select(t=>t?.ToString().Trim()??"").Where(t=>t.Length>0).ToArray();
                result.Plates.Add(new(SourceId("glamaholic/"+identity),name,folder,tags,items));
            }
        }
    }

    public static uint CollectionId(string url)
    {
        if (Uri.TryCreate(url.Trim(),UriKind.Absolute,out var uri) && uri.Scheme == "https"
            && uri.Host == "ffxiv.eorzeacollection.com" && uri.AbsolutePath.Split('/') is ["","glamour",var id,..]
            && uint.TryParse(id,NumberStyles.None,CultureInfo.InvariantCulture,out var number) && number>0)
            return number;
        throw new FormatException("Paste an Eorzea Collection glamour link.");
    }

    public static string Normalize(string name) =>
        System.Net.WebUtility.HtmlDecode(name).Replace('\u00a0',' ').Replace('\u2019','\'').Trim();

    public static WardrobeImport Collection(uint id,string json,Func<string,string,uint?> itemId,Func<string,byte?> dyeId)
    {
        var outfit = JsonNode.Parse(json) as JsonObject;
        if ((uint?)outfit?["id"] != id || outfit?["gear"] is not JsonObject gear)
            throw new FormatException("Eorzea Collection did not return the requested outfit.");
        var result = new WardrobeImport();
        var items = new Dictionary<string,ImportedItem>();
        foreach (var (key, _, slot, _) in Slots)
        {
            if (gear[slot] is not JsonObject piece || piece["name"]?.ToString() is not { Length: > 0 } label) continue;
            var item = itemId(key,Normalize(label));
            if (item == null) { result.Warnings.Add($"{slot}: {label}"); continue; }
            var dyes = piece["dyes"]?.ToString().Split(',') ?? [];
            items[key] = new(item.Value,Dye(0),Dye(1));
            byte Dye(int index)
            {
                if (index >= dyes.Length) return 0;
                var name = dyes[index].Trim().Replace('-',' ');
                if (name.Length == 0 || name.Equals("none",StringComparison.OrdinalIgnoreCase)) return 0;
                if (dyeId(name) is {} stain) return stain;
                result.Warnings.Add($"{slot} dye {index+1}: {name}");
                return 0;
            }
        }
        if (items.Count==0) throw new FormatException("No matching equipment in this outfit. "+string.Join(", ",result.Warnings));
        result.Plates.Add(new(SourceId("ec/"+id),Label(outfit["name"],"Eorzea Collection outfit"),"",[],items));
        return result;
    }
}
