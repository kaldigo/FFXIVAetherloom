# Aetherloom UI plan

Aetherloom is a separate Dalamud companion to Glamourer. Open it with `/aetherloom`.

## Companion and Glamourer designs

Use the new window for the everyday loop of choosing an outfit, editing it, and applying it. A Settings / Advanced action opens the existing Glamourer window with its full controls. The companion stores the full outfit and maps each character to an ownership-filtered Glamourer copy by ID. Glamourer job automation points directly to that copy. Refresh it in place when the plan or ownership changes. The companion handles zone overrides.

Mark generated Glamourer designs with an ordinary design tag, provisionally `ui-wardrobe`. Wardrobe adds it automatically whenever it creates a design. Importing an existing upstream design would require an explicit copy of the full plan before applying the ownership filter. Use an exact, case-insensitive tag match for membership.

In the full Designs window, `-t:ui-wardrobe` filters out tagged designs and `t:ui-wardrobe` finds them. The existing search matches tag substrings, so reserve this tag text for membership.

## Window layout

The fork exposes `Aether.Glamourer.UpdateDesign(Guid id, string json) -> int` on the framework thread. Pass the complete appearance JSON, for example the edited result of `Glamourer.GetDesignJObject` serialized as text. The call replaces appearance, materials and apply flags, keeps the target design's identity, metadata, links and mod associations, and queues a save. Disable both `Apply` and `ApplyStain` on a missing slot. The result uses Glamourer's existing error codes, including `NothingDone` for a locked design.

| Area | Contents |
| --- | --- |
| Left sidebar | Wardrobe-tagged designs in a flat list, regardless of folder. Filter by additional tags. Clearing filters restores the full Wardrobe list. |
| Design header | Design name, a read-only job tag derived from the selected main-hand weapon, editable outfit tags, and Apply to character. Use the remaining header width for tags. Keep these controls visible on every tab. |
| Gear tab | Equipped appearance for weapons and armor, both dye channels where supported, and per-slot application controls. Show the existing missing-item border and tooltip beside the affected selector. Place Copy to glamour plate here. |
| Accessories tab | Earrings, necklace, bracelets, right ring and left ring. Use the same slot controls and missing-item border as Gear. Read each item’s dye-channel count and display zero, one or two editable swatches accordingly. |
| Appearance tab | Hair and face paint. Proposed controls are hairstyle, hair color, highlights and their color, face paint pattern, reversal and paint color. Allow hair and face paint to be included independently. |
| Assignments tab | Choose the jobs and zones that automatically apply this design for the current character. Show existing assignments and let the user add, transfer, or clear one. |

Derive the job tag from the weapon’s game-data job eligibility. Changing the weapon updates the tag immediately. Include the derived job in sidebar filtering. Proposed edge behavior: omit the chip when there is no main-hand weapon and list the eligible jobs when a weapon is shared. Editing a weapon or an outfit tag never changes an automatic assignment.

The right-hand pane belongs to the window across all four tabs. Frame the entire outfit and weapon for Gear, allow closer framing for jewelry, and offer a face view for Appearance. Include rotation, zoom and a way back to the full outfit. Use the flat grey native Fitting Room lighting and proportions as the in-game reference. The preview shows the planned pieces, including missing pieces, without applying them to the player or a plate.

The generated concepts show [Gear](wardrobe/gear.png), [Accessories with dye channels](wardrobe/accessories.png), and [Appearance](wardrobe/appearance.png). The Gear render shows the current header and four-tab navigation. Accessories and Appearance are earlier panel explorations.

Reuse Glamourer’s existing design tags. A design can have several tags. Proposed filter behavior is to require all selected tags, so selecting another tag narrows the list.

## Design assignments

Assign or clear jobs and zones in the selected design’s Assignments tab. Any number of designs can carry the same weapon-derived job tag. Only the explicitly assigned design automatically applies for that job. Each character can assign several jobs or zones to one design. Each individual job or zone has one assigned design per character. Assigning Warrior to Mountain Gear automatically transfers just Warrior from Winter Coat. Show which design lost the assignment.

An assigned zone takes priority over the current job. A job change inside that zone keeps the zone design active. On leaving, check the destination zone first. If it has an assignment, use that design. Otherwise apply the design assigned to the character’s **current** job, including a job selected during the visit.

## Applying a design

**Apply to character** and Glamourer job automation cover enabled gear, standard dyes, hair and face paint. An appearance-only design can apply independently of gear. Handling existing designs with additional advanced fields is deferred.

For gear, count exact items in the four inventory pages, wearable Armoury Chest slots, equipped gear, glamour dresser and armoire. Persist each source separately per character and replace its snapshot only after a complete read. Count the actual item ID even when a different glamour is displayed. Missing pieces stay in the plan. Skip their slot and dyes when applying. Event and Mog Station exemptions follow the existing normal-acquisition precedence. Opening storage refreshes ownership indicators. Newly eligible pieces apply on the next manual apply or automation run.

**Copy to glamour plate** is a planned action for the native plate editor at the dresser. Show the destination plate number, populate enabled gear slots from items actually available in the live dresser or armoire, and leave missing or disabled slots intact. Character-only exemptions cannot supply a physical plate item. Use the game’s dye availability rules and leave the plate open for review and Save. Hair and face paint belong exclusively to character application.

## Delivery and acceptance

1. Test the saved-design IPC with a direct job assignment. Start with boots and their dyes disabled, enable both on the same ID, and check that the next job switch applies them. Restart and verify the assignment and updated flags persist. Then port the archived WardrobePreviewPanel.cs.txt into the companion for the embedded preview trial. Start with Try On closed. Test in a party and in a duty as well as solo. Compare a design with visibly different gear, accessory dyes, hair and face paint against the player. Exercise zoom and rotation, open a different native glamour plate, then stop and reopen the prototype. Check both views independently and check that the world actor and plate retain their own appearance. The prototype uses a shared portrait slot and pauses for zoning and party portraits, then resumes when the slot is available. Repeat after closing the debug panel and reloading the plugin.
2. If the embedded view cannot run independently or coexist reliably with the native plate editor, remove the pane, reclaim its width, and offer a **Try On** button to open the native Fitting Room for the planned outfit. Native hair and face-paint preview in this fallback is unverified.
3. Build the sidebar, tag editing and the four-tab editor. Verify automatic tagging on creation, manual inclusion and removal through the full window, and filtering within the Wardrobe list. Verify that two bow designs both appear under Bard, assigning one selects only that design for automation, and changing a weapon updates its tag without transferring any assignment.
4. Add scoped character application and assignments. Test mixed owned/missing gear, a hair-only edit, reassignment of one job or zone without disturbing other assignments, character isolation, moving between assigned zones, and returning to the current job after a job change inside a zone.
5. Add native plate transfer. Verify the selected destination, partial ownership, unavailable dyes, and preservation of skipped slots in game.

Eorzea Collection URL import is a candidate for the longer-term Glamaholic replacement. An import should create an editable planned design with missing pieces visible. Its place in the release order is open.

## Decisions still open

- Decide what happens when leaving an assigned zone if neither the destination zone nor the current job has an assignment.
- Decide how Wardrobe assignments coexist with advanced automation.
- Decide how to label the route to the existing full Glamourer window.
- Decide how an explicitly empty gear slot should behave when copying onto an occupied native plate slot.
