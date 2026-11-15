namespace Aetherloom;

internal sealed record OwnershipSnapshot(
    HashSet<uint>? Dresser = null,
    HashSet<uint>? Armoire = null,
    HashSet<uint>? Bags = null,
    HashSet<uint>? Armoury = null,
    HashSet<uint>? Equipped = null)
{
    internal OwnershipSnapshot RefreshArmoire(bool windowReady, Func<HashSet<uint>?> readLoadedContents)
    {
        if (!windowReady || readLoadedContents() is not {} contents || Armoire?.SetEquals(contents) == true)
            return this;
        return this with { Armoire = contents };
    }

    internal string Availability(uint itemId, bool exempt, bool currentCharacter)
    {
        if (currentCharacter)
        {
            if (Bags?.Contains(itemId) == true) return "Inventory";
            if (Armoury?.Contains(itemId) == true) return "Armoury Chest";
            if (Equipped?.Contains(itemId) == true) return "Equipped";
            if (Dresser?.Contains(itemId) == true) return "Glamour Dresser";
            if (Armoire?.Contains(itemId) == true) return "Armoire";
        }

        if (exempt) return "Event / Mog Station exemption";
        if (!currentCharacter || Bags == null || Armoury == null || Equipped == null || Dresser == null || Armoire == null)
            return "Unconfirmed";
        return "Missing";
    }
}
