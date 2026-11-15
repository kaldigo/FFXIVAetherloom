namespace Aetherloom;

internal sealed record WardrobeTag(string Group,string Value,string Raw);

internal static class WardrobeMetadata
{
    public const string Gender="Designed for", Intended="Intended for", Weapon="Weapon job", General="Tags";
    public static readonly string[] Categories=["Classification","Style","Theme","Color",General];
    public static readonly string[] Filters=[Gender,Intended,Weapon,..Categories];
    private static readonly Dictionary<string,string> Prefix=new()
    {
        [Gender]="gender", [Intended]="intended", ["Classification"]="classification", ["Style"]="style",
        ["Theme"]="theme", ["Color"]="color", [General]="tag"
    };
    internal static readonly Dictionary<string,string[]> Audience=new()
    {
        ["Roles"]="All Classes|Tanks|Healers|Melee DPS|Ranged DPS|Caster DPS|Disciples of War|Disciples of Magic|Disciples of War and Magic|Disciples of the Hand|Disciples of the Land|Disciples of the Hand and Land".Split('|'),
        ["Jobs"]="Paladin|Warrior|Dark Knight|Gunbreaker|White Mage|Scholar|Astrologian|Sage|Monk|Dragoon|Ninja|Samurai|Reaper|Viper|Beastmaster|Bard|Machinist|Dancer|Summoner|Black Mage|Red Mage|Pictomancer|Blue Mage".Split('|'),
        ["Crafters"]="Carpenter|Armorer|Leatherworker|Alchemist|Blacksmith|Goldsmith|Weaver|Culinarian".Split('|'),
        ["Gatherers"]=["Botanist","Fisher","Miner"]
    };
    private static readonly string[] Abbreviations="PLD WAR DRK GNB WHM SCH AST SGE MNK DRG NIN SAM RPR VPR BST BRD MCH DNC SMN BLM RDM PCT BLU CRP ARM LTW ALC BSM GSM WVR CUL BTN FSH MIN".Split(' ');
    internal static readonly Dictionary<string,string> JobAliases=Audience.Where(g=>g.Key!="Roles").SelectMany(g=>g.Value)
        .Zip(Abbreviations).ToDictionary(p=>p.First,p=>p.Second,StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string,string[]> Defaults=new()
    {
        [Gender]=["Unspecified","Male","Female","Both"],
        [Intended]=Audience.Values.SelectMany(v=>v).ToArray(),
        ["Classification"]="Athletic|Cool|Cute|Divine|Elegant|Fashionable|Glamorous|Heroic|Sexy|Strong|Sweet|Villainous|Youthful".Split('|'),
        ["Style"]="Ancient|Bohemian|Casual|Chic|Cosplay|Dark|Earth|Eastern|Fantasy|Fire|Formal|Futuristic|Goth|Grunge|Historical|Holy|Ice|Lightning|Magical|Medieval|Modern|Retro|Spiritual|Steampunk|Tribal|Vacation|Vintage|Water|Wind".Split('|'),
        ["Theme"]="Academic Clothes|Adventurer|Autumn Outfit|Battle Gear|Crafter|Cyber Outfit|Desert Outfit|Eternal Bonding|Final Fantasy|Forest Outfit|Gatherer|House Wear|Idle Clothes|Military Gear|Nature Outfit|Ninja|Party Outfit|Pirate|Royalty|Seasonal Event|Special Occasions|Sportswear|Spring Outfit|Summer Outfit|Swimwear|Wild West Outfit|Winter Outfit".Split('|'),
        ["Color"]="Beige|Black|Blue|Brown|Gold|Green|Grey|Metallic|Monochromatic|Neon|Orange|Pastel|Pink|Purple|Red|Silver|Turquoise|Vibrant|White|Yellow".Split('|'),
        [General]=[]
    };
    private static readonly Dictionary<string,string> LegacyGroups=Categories.SelectMany(g=>Defaults[g].Select(v=>(v,g)))
        .ToDictionary(p=>p.v,p=>p.g,StringComparer.OrdinalIgnoreCase);

    internal static WardrobeTag Decode(string raw)
    {
        int split=raw.IndexOf(':');
        if(split>0 && raw[(split+1)..].Trim() is {Length:>0} value)
            foreach(var (group,prefix) in Prefix)
                if(raw[..split].Equals(prefix,StringComparison.OrdinalIgnoreCase))return new(group,value,raw);
        return new(LegacyGroups.GetValueOrDefault(raw,General),raw,raw);
    }

    internal static string Encode(string group,string value)
        => group==General && Decode(value) is {Group:General} tag && tag.Value==value ? value : Prefix[group]+":"+value;

    internal static string Single(string group,IEnumerable<WardrobeTag> tags)
        => tags.FirstOrDefault(t=>t.Group==group)?.Value ?? Defaults[group][0];

    internal static bool ContainsText(string value,string query)=>value.Contains(query,StringComparison.OrdinalIgnoreCase)
        || (JobAliases.TryGetValue(value,out var abbr) && abbr.Contains(query,StringComparison.OrdinalIgnoreCase));

    internal static bool Matches(IEnumerable<WardrobeTag> tags,Dictionary<string,HashSet<string>> filters,Func<IEnumerable<string>> weapons)
    {
        foreach(var (group,choices) in filters)
        {
            if(choices.Count==0)continue;
            if(group==Weapon){if(!weapons().Any(choices.Contains))return false;}
            else if(group is Gender or Intended){if(!choices.Contains(Single(group,tags)))return false;}
            else if(!tags.Any(t=>t.Group==group && choices.Contains(t.Value)))return false;
        }
        return true;
    }
}
