using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeBase
{
    // Glamourer's serialized Customize field names and the corresponding player customization bits.
    internal static readonly (string Key,int Index,byte Mask)[] Appearance=
    [
        ("Race",0,0xFF), ("Gender",1,0xFF), ("BodyType",2,0xFF),
        ("Height",3,0xFF), ("Clan",4,0xFF), ("Face",5,0xFF),
        ("Hairstyle",6,0xFF), ("Highlights",7,0x80), ("SkinColor",8,0xFF),
        ("EyeColorRight",9,0xFF), ("HairColor",10,0xFF), ("HighlightsColor",11,0xFF),
        ("FacialFeature1",12,0x01), ("FacialFeature2",12,0x02), ("FacialFeature3",12,0x04),
        ("FacialFeature4",12,0x08), ("FacialFeature5",12,0x10), ("FacialFeature6",12,0x20),
        ("FacialFeature7",12,0x40), ("LegacyTattoo",12,0x80), ("TattooColor",13,0xFF),
        ("Eyebrows",14,0xFF), ("EyeColorLeft",15,0xFF), ("EyeShape",16,0x7F),
        ("SmallIris",16,0x80), ("Nose",17,0xFF), ("Jaw",18,0xFF),
        ("Mouth",19,0x7F), ("Lipstick",19,0x80), ("LipColor",20,0xFF),
        ("MuscleMass",21,0xFF), ("TailShape",22,0xFF), ("BustSize",23,0xFF),
        ("FacePaint",24,0x7F), ("FacePaintReversed",24,0x80), ("FacePaintColor",25,0xFF),
    ];
    internal static readonly string[] OutfitFields=["Hairstyle","Highlights","HairColor","HighlightsColor","FacePaint","FacePaintReversed","FacePaintColor"];

    // Clan and gender changes can make Glamourer normalize other appearance fields.
    internal static void RecordApplied(JObject previous,JObject next,JObject before,JObject after,JObject? baseLook,JObject appliedLook)
    {
        bool normalize=baseLook?["Clan"]?.Value<bool>("Apply")==true || baseLook?["Gender"]?.Value<bool>("Apply")==true;
        foreach(var (key,_,_) in Appearance)
        {
            if(after[key]?["Value"] is not {} actual || before[key]?["Value"] is not {} visible)continue;
            if(next[key] is JObject tracked)
            {
                if(appliedLook[key]?.Value<bool>("Apply")==true)
                    tracked["Applied"]=actual.DeepClone();
                else if(normalize && !JToken.DeepEquals(actual,visible))
                {
                    if(!JToken.DeepEquals(tracked["Applied"],visible))tracked["Before"]=visible.DeepClone();
                    tracked["Applied"]=actual.DeepClone();
                    tracked["Base"]=true;
                }
                continue;
            }
            var old=previous[key] as JObject;
            var baseline=old!=null && JToken.DeepEquals(old["Applied"],visible)?old["Before"]:visible;
            if(normalize && baseline!=null && !JToken.DeepEquals(baseline,actual))
                next[key]=new JObject{["Before"]=baseline.DeepClone(),["Applied"]=actual.DeepClone(),["Base"]=true};
        }
    }

    internal static bool TryValue(string key,byte current,byte mask,JObject look,JObject? previous,out byte value)
    {
        if(look[key] is JObject field && field.Value<bool>("Apply"))
        {value=field.Value<byte>("Value");return true;}
        return TryOriginal(key,current,mask,previous,out value);
    }

    internal static bool TryOriginal(string key,byte current,byte mask,JObject? previous,out byte value)
    {
        if(previous?[key] is JObject field && field.Value<byte?>("Applied")== (current&mask)
            && field.Value<byte?>("Before") is {} original)
        {value=original;return true;}
        value=current;return false;
    }

    internal static void RestorePreviewAppearance(Span<byte> bytes,JObject? previous)
    {
        if(previous==null)return;
        foreach(var (key,index,mask) in Appearance)
            if(TryOriginal(key,bytes[index],mask,previous,out var original))
                bytes[index]=(byte)((bytes[index]&~mask)|(original&mask));
    }

    // Hrothgar use the lip colour byte for their fur pattern. Preserve the linked base there.
    internal static JObject WithLipColor(JObject look,JObject? shade,byte race,JObject? previous)
    {
        if(shade?.Value<bool>("Apply")!=true)return look;
        TryValue("Race",race,0xff,look,previous,out var targetRace);
        if(targetRace is 0 or 7)return look;
        var result=(JObject)look.DeepClone();
        result["LipColor"]=shade.DeepClone();
        result["Lipstick"]=new JObject{["Value"]=128,["Apply"]=true};
        return result;
    }

    internal static JObject Compose(JObject? baseLook,JObject outfit)
    {
        var result=new JObject();
        foreach(var (key,_,_) in Appearance)
            if(baseLook?[key] is JObject field && field.Value<bool>("Apply"))result[key]=field.DeepClone();
        // Glamourer changes race through clan. Carry the matching race into the native preview too.
        if(result["Clan"] is JObject clan && clan.Value<int>("Value") is >=1 and <=16 and var tribe)
            result["Race"]=new JObject{["Value"]=(tribe+1)/2,["Apply"]=true};
        foreach(var key in OutfitFields)
            if(outfit[key] is JObject field && field.Value<bool>("Apply"))result[key]=field.DeepClone();
        return result;
    }
}
