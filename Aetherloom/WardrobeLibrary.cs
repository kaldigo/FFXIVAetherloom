namespace Aetherloom;

internal sealed record LibraryDesign(Guid Id,string Name,string[] Tags,string Folder="",DateTimeOffset LastEdit=default,ulong MainHand=0)
{
    private WardrobeTag[]? metadata;
    public WardrobeTag[] Metadata=>metadata??=Tags.Where(WardrobeLibrary.VisibleTag).Select(WardrobeMetadata.Decode).ToArray();
}
internal static class WardrobeLibrary
{
    internal static string StoredName(string parent,string label)=>parent.Length==0?"[AL] "+label:label;
    internal static string PathFor(string parent,string label)=>parent.Length==0?StoredName(parent,label):parent+"/"+label;
    internal static bool Within(string path,string ancestor)=>path==ancestor || path.StartsWith(ancestor+"/",StringComparison.Ordinal);
    internal static bool VisibleTag(string tag)=>!string.Equals(tag,Tag,StringComparison.OrdinalIgnoreCase)
        && !string.Equals(tag,LegacyTag,StringComparison.OrdinalIgnoreCase);
    internal const string Tag="aetherloom";
    internal const string LegacyTag="ui-wardrobe";
    internal static bool Tagged(IEnumerable<string> tags,string tag)=>tags.Contains(tag,StringComparer.OrdinalIgnoreCase);
    internal static bool Managed(string path)=>path.StartsWith("[AL]",StringComparison.OrdinalIgnoreCase) && path.Split('/')[0][4..].Trim().Length>0;
    internal static string Parent(string path)=>path.LastIndexOf('/') is var split && split>=0?path[..split]:"";
    internal static string Label(string path)=>path.Contains('/')?path[(path.LastIndexOf('/')+1)..]:path[4..].TrimStart();
}
