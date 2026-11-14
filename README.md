# Aetherloom

A standalone Dalamud companion for planning outfits with Glamourer. Open `/aetherloom`, select a saved Glamourer design and inspect the availability of each equipment item. **Refresh designs** reloads design edits. Item availability refreshes while the window is open or closed.

Each character has their own inventory, Armoury Chest, equipped gear, glamour dresser and armoire snapshots. The plugin counts the actual item even if it has another glamour applied. It retains the last complete snapshot for each source across sessions and can seed dresser and armoire data from the earlier Glamourer fork. Open both storage windows once if a piece shows **Unconfirmed**. Event and Mog Station exemptions use the catalog in `Aetherloom/WardrobeExceptions.json`. An overlapping normal acquisition source takes precedence.

To try the embedded preview, open the window solo or in a party, including during a duty, select an outfit that looks different from your equipped gear, and click **Start embedded preview**. Rotate and zoom, then switch designs. Open the Fitting Room or a glamour plate alongside it to compare the two views. The prototype uses the eighth portrait target and releases it during zoning and when party portraits need it. It resumes the selected design automatically when the character and slot are ready. If the texture fails to load, the window reports its portrait state after 15 seconds.

The editor, filtered copies for Glamourer automation, and transfer to native glamour plates are described in the [UI plan](docs/wardrobe-ui-plan.md). Plate transfers require actual dresser or armoire stock.

## Development

Build with `dotnet build Aetherloom/Aetherloom.csproj -c Release` using Dalamud API 15 development references. Add the generated `Aetherloom/bin/Release/Aetherloom.dll` to Dalamud's dev plugin locations and enable Glamourer. Run the inventory and cache checks with `dotnet run --project tests/Ownership` and the acquisition tests with `python -m unittest discover -s tests`.
