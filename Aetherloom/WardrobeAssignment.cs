using Newtonsoft.Json.Linq;

namespace Aetherloom;

internal readonly record struct WardrobeAssignment(ulong Character,uint Job,uint Zone,Guid Design)
{
    internal static WardrobeAssignment? Resolve(JObject? bindings,ulong character,uint job,uint zone,Func<Guid,bool> visible)
    {
        if(character==0 || job==0 || zone==0)return null;
        var design=Find("Zones",zone) ?? Find("Jobs",job);
        return design is {} id ? new(character,job,zone,id) : null;

        Guid? Find(string type,uint key)
            => Guid.TryParse((string?)bindings?[type]?[key.ToString()],out var id) && visible(id) ? id : null;
    }
}
