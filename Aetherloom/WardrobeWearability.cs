using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal static class WardrobeWearability
{
    // Bits 0–7 are playable races in game order. Bits 8 and 9 are male and female.
    internal const int Everyone=0x3ff;

    internal static bool Allows(int mask,byte race,byte sex)
        => race is >=1 and <=8 && sex<=1 && (mask & (1<<(race-1)))!=0 && (mask & (1<<(8+sex)))!=0;

    internal static int DesignMask(JObject plan,IEnumerable<string> keys,Func<ulong,int> itemMask)
    {
        if(plan["Equipment"] is not JObject equipment)return 0;
        var slots=keys.Select(key=>equipment[key]).OfType<JObject>();
        if(plan["AdditionalMainHands"] is JObject hands)
            slots=slots.Concat(hands.Properties().Select(p=>p.Value).OfType<JObject>());
        int mask=Everyone;
        foreach(var slot in slots)
            if(slot.Value<bool>("Apply") && slot.Value<ulong?>("ItemId") is >0 and var id)
                mask&=itemMask(id);
        return mask;
    }
}
