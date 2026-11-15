# Aetherloom

Open `/aetherloom` to create, organize and apply outfits. Aetherloom stores its designs and folders in `wardrobe-plans.json`. Glamourer handles character state and supplies linked appearance bases.

On the first wardrobe access after updating, enable the Glamourer fork so Aetherloom can read the existing library. It copies tagged designs, uses existing Aetherloom plans for outfit contents, and preserves wardrobe folders (including empty ones), tags, design IDs, favourites, assignments and linked bases. Tagged designs outside wardrobe folders appear at the root. Aetherloom saves a pre-migration copy of the existing file as `wardrobe-plans.json.pre-local.bak`. If a required design is unavailable, migration retries and Steward treats the source as unavailable until the complete library is saved.

After migration, creation, imports, edits and deletions operate locally. Existing Glamourer designs and folders stay in Glamourer. Steward reads the local wardrobe, including alternate main hands, even when the editor is closed.

Each character has their own inventory, Armoury Chest, equipped gear, glamour dresser and armoire snapshots. The plugin counts the actual item even if it has another glamour applied. It retains the last complete snapshot for each source across sessions and can seed dresser and armoire data from the earlier Glamourer fork. Open both storage windows once if a piece shows **Unconfirmed**. Event and Mog Station exemptions use the catalog in `Aetherloom/WardrobeExceptions.json`. An overlapping normal acquisition source takes precedence.

Select a design to preview it on a copy of your character. Drag with the left mouse button to rotate, drag with the right button to pan, and scroll over the preview to zoom. The preview pauses during zoning and party portraits, then resumes when the character and slot are available.

Use the global Manual / Assignments switch to control Aetherloom outfit automation.

## Importing designs

Open **Import** in the wardrobe sidebar. Paste an Eorzea Collection glamour URL to import its gear and both dye channels into the selected folder. The result lists any item or dye names the game could not match.

**Import saved library** reads Glamaholic from the current Dalamud profile and copies plates, tags and nested folders into **Glamaholic** at the wardrobe root. Slashes in names use a full-width slash so they stay part of the name. Both importers skip designs already copied, preserving subsequent edits.

## Favourites and linked appearances

Star a design beside its title and use **Favourites only** in the sidebar. To share an appearance, tag a player-character design outside the wardrobe with `aetherloom-base` in Glamourer, then choose it under **Details / Base design** in Aetherloom. Preview, manual application and assignments layer the enabled character customization fields under the outfit. Switching outfits releases the old base. **Only available glamours** checks against your currently displayed race and gender.

## Dalamud IPC

Call these synchronous endpoints on the framework thread. Both lists map stable design GUIDs to display names and support duplicate names.

| Endpoint | Input | Result |
| --- | --- | --- |
| `Aetherloom.GetDesigns` | None | `Dictionary<Guid,string>` of wardrobe designs |
| `Aetherloom.GetFavouriteDesigns` | None | Same mapping restricted to favourites |
| `Aetherloom.GetAssignmentMode` | None | Boolean, true for Assignments |
| `Aetherloom.SetAssignmentMode` | Boolean | Resulting global mode |
| `Aetherloom.ToggleAssignmentMode` | None | Resulting global mode |
| `Aetherloom.ApplyDesign` | Guid | True on success. Applies to the local player and enters Manual. |
| `Aetherloom.RevertAll` | None | True on success. Restores actual gear and appearance and enters Manual. |

## Development

Build with `dotnet build Aetherloom/Aetherloom.csproj -c Release` using Dalamud API 15 development references. Add the generated `Aetherloom/bin/Release/Aetherloom.dll` to Dalamud's dev plugin locations and enable Glamourer. Run the inventory and cache checks with `dotnet run --project tests/Ownership` and the acquisition tests with `python -m unittest discover -s tests`.
