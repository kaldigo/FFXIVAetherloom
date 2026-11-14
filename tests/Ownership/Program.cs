using System.Text.Json;
using Aetherloom;

const uint boots = 400;
void Check(OwnershipSnapshot snapshot, string expected, bool current = true, bool exempt = false)
{
    var actual = snapshot.Availability(boots, exempt, current);
    if (actual != expected) throw new Exception($"Expected {expected}, got {actual}.");
}

var empty = new OwnershipSnapshot([], [], [], [], []);
Check(empty, "Missing");
Check(empty with { Bags = [boots] }, "Inventory");
Check(empty with { Bags = [], Armoury = [boots] }, "Armoury Chest");
Check(empty with { Equipped = [boots] }, "Equipped");
Check(empty with { Dresser = [boots] }, "Glamour Dresser");
Check(empty with { Armoire = [boots] }, "Armoire");
Check(empty with { Armoury = null }, "Unconfirmed");
Check(empty with { Armoury = null, Bags = [boots] }, "Inventory");
Check(empty with { Bags = [boots] }, "Unconfirmed", current: false);
Check(empty, "Event / Mog Station exemption", exempt: true);
var stored = JsonSerializer.Serialize(empty with { Armoury = [boots] });
Check(JsonSerializer.Deserialize<OwnershipSnapshot>(stored)!, "Armoury Chest");
var legacy = JsonSerializer.Deserialize<OwnershipSnapshot>("""{"Dresser":[400],"Armoire":[]}""")!;
Check(legacy, "Glamour Dresser");
Check(legacy with { Dresser = [] }, "Unconfirmed");
Console.WriteLine("Ownership checks passed.");
