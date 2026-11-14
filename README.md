# Aetherloom

A standalone Dalamud companion for planning outfits with Glamourer. Open `/aetherloom`, select a saved Glamourer design and inspect the availability of each equipment item. **Refresh designs** reloads design edits. Item availability refreshes while the window is open or closed.

Each character has their own inventory, Armoury Chest, equipped gear, glamour dresser and armoire snapshots. The plugin counts the actual item even if it has another glamour applied. It retains the last complete snapshot for each source across sessions and can seed dresser and armoire data from the earlier Glamourer fork. Open both storage windows once if a piece shows **Unconfirmed**. Event and Mog Station exemptions use the catalog in `Aetherloom/WardrobeExceptions.json`. An overlapping normal acquisition source takes precedence.

Open `/aetherloom` and click **Preview current gear** to load a copy of your character as currently equipped. Drag with the left mouse button to rotate, drag with the right button to pan, and scroll over the preview to zoom. Stop and restart the preview to pick up gear changes. The preview pauses during zoning and party portraits, then resumes when the character and slot are available.

The editor, filtered copies for Glamourer automation, and transfer to native glamour plates are described in the [UI plan](docs/wardrobe-ui-plan.md). Plate transfers require actual dresser or armoire stock.

## Development

Build with `dotnet build Aetherloom/Aetherloom.csproj -c Release` using Dalamud API 15 development references. Add the generated `Aetherloom/bin/Release/Aetherloom.dll` to Dalamud's dev plugin locations and enable Glamourer. Run the inventory and cache checks with `dotnet run --project tests/Ownership` and the acquisition tests with `python -m unittest discover -s tests`.
