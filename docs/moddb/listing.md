# BetterMods - mod DB publishing kit

Everything needed to fill in the BetterMods page on mods.vintagestory.at. The page body is
`description.html` in this folder.

## Summary (required, max 100 characters)

Shown on listing cards and link previews, not on the page itself. Pick one:

| # | Summary | Chars |
|---|---|---|
| 1 | `Switch between mod profiles in one click, pick mod versions, and update mods from the Mods screen.` | 98 |
| 2 | `Adds mod profiles, a version picker and mod DB update checks to the Mods screen. Client-side.` | 93 |
| 3 | `Save sets of enabled mods as profiles; switch in one click from the menu or in game. Client-side.` | 97 |

Recommendation: **1** for the feature spread, **3** if the listing should lead with what it does for
the player.

## Fields

| Field | Value |
|---|---|
| Name | BetterMods |
| Mod id | `bettermods` (must match `modinfo.json`) |
| Side | Client |
| Tags | see below |
| Homepage / Wiki / Issues / Source / Donate | Fill in whichever exist - each one that is filled appears as a tab on the page. **Issues** and **Source** are the most useful (the description points bug reports at "the Issues tab, where linked"). Leave the rest empty rather than pointing them at placeholders. |
| Logo | `screenshots/logo-480x320.png`, uploaded as a screenshot and selected as the logo - see below |
| License | 0BSD. The mod DB has no licence field; the description ends with a License section, and every release zip carries `LICENSE` and `THIRD-PARTY-NOTICES.md`. If **Source** is linked, those files are visible there too. |

Suggested **tags** (use whichever of these the DB's tag list offers): `Utility`, `QoL`, `Client-side`,
`GUI`, `Mod management`. Avoid tags that imply gameplay or server content.

**Changelogs** belong to each release in the **Files** tab (one per uploaded zip), not in the
description. On every release, set the game version tags to the versions it actually supports:
1.3.x is `1.23.0-pre.1` and later 1.23 builds; the 1.2.0 file is the one for 1.22.

## Screenshots

The images are ready in `screenshots/` (PNG, native game pixels, cropped, no edits beyond cropping;
each well under the DB's 2 MB and 1920x1080 px limits; the hero shot is scaled 97.6% and the playstyles shot trimmed 2 px to fit 1080). The DB takes up to 12. Each `SCREENSHOT_URL_n` in
`description.html` is one file - the number matches the file's prefix:

| File | Placeholder | Caption / what it shows |
|---|---|---|
| `logo-480x320.png` | - (select as **logo**) | Profile tabs (Testing / Playing / +) above four mod cards, two with update buttons. Downscaled from the hero screenshot to exactly 480x320. |
| `01-mods-screen.png` | `SCREENSHOT_URL_1` | Main-menu Mods screen: profile tabs above the mod list, mod cards with update buttons, the update status with Update all, and Apply Profile. |
| `02-rename-panel.png` | `SCREENSHOT_URL_2` | Right-clicking a tab opens the rename panel under it: name field, Delete, Enter saves, Esc cancels. |
| `03-version-dropdown.png` | `SCREENSHOT_URL_3` | Two installed versions of one mod: the version drop-down open under the version label, the one in use ticked. |
| `04-update-and-retracted.png` | `SCREENSHOT_URL_4` | Yellow update markers with Update to buttons: updates for an older game version show (1.22), an exact-version update does not, and a retracted release shows (Retracted). Two crops of one screenshot stacked (the two cards between them left out). |
| `05-tooltip.png` | `SCREENSHOT_URL_5` | Card tooltip: name, mod id, version and side on one line; authors; description; then dependencies, an update for an older game version and the retraction reason. |
| `06-world-list-tooltip.png` | `SCREENSHOT_URL_6` | Singleplayer world list: hovering a world shows, at the bottom of its tooltip, the BetterMods profile it is bound to and the mods it last loaded with. |
| `07-add-shortcut.png` | `SCREENSHOT_URL_7` | BetterMods' own card with the Add Shortcut button, greyed out once the shortcut exists. |
| `08-tooltip-update.png` | `SCREENSHOT_URL_8` | Tooltip of a mod with an update tagged for the exact game version: the green Update available line. |
| `09-saved-playstyles.png` | `SCREENSHOT_URL_9` | Create New World with Save and Delete playstyle, and a saved custom playstyle. |

**Upload order:** `logo-480x320.png` first (then select it as the logo), then `01` to `09` in file
order, so the gallery opens on the hero shot. That is 10 of the 12 slots.

**Logo.** The DB wants **480x480** or **480x320**; `logo-480x320.png` is the 480x320 one (it matches
the listing cards). It is uploaded as a screenshot and counts toward the 12.

**BetterMods has no `modicon.png` yet**, so its own card (shots 01, 07 and the logo) shows the game's
generic tree-and-gear icon - the same one other icon-less mods get. Making a proper `modicon.png`
(and retaking those three shots afterwards) is recommended; it would also make the logo far more
recognisable.

### Optional - add later

Not captured yet. Their placeholders were removed from `description.html`; when one exists, add it
back as `10-...png` / `SCREENSHOT_URL_10` and so on, in the section named (only two slots are left,
so pick the two that matter most):

| Shot | What to capture | Section in `description.html` |
|---|---|---|
| In-game dialog | **In game**, the Mod Profiles dialog over a world (bind its key under Settings > Controls first - it has none by default): tabs, cards with switches, the notice line, Apply Profile / Close. | "The in-game Mod Profiles dialog" |
| Fix dependencies | A card with the yellow **Fix dependencies** button, hovered so the tooltip lists the dependency problems. | "Fix dependencies" |
| World-start prompt | Starting a singleplayer world with the wrong profile: headline, list of missing mods, and **Cancel / Play with ... / Switch to ...**. | "Worlds remember their profile and mods" |
| Open mod page question | The first-click question: **Cancel / Always trust mods.vintagestory.at / Open**. | "Open mod page and trusted links" |

Take them at a GUI scale where text is easy to read, then crop to the feature with a little room
around it (no upscaling).

## Pasting the description

1. Upload the screenshots first (the **Screenshots** / images section of the mod's edit page), in
   the order above. Wait for each upload to finish.
2. For each uploaded image, copy its address: open the image (or right-click it > **Copy image
   address**). It will be a `https://moddbcdn.vintagestory.at/...` URL.
3. Open `description.html` in a text editor and replace each `SCREENSHOT_URL_n` with the matching
   address from the screenshot table. Keep the quotes: `src="https://moddbcdn.vintagestory.at/..."`.
   Never paste an image as a `data:` URI - it counts against the 65,535-byte limit.
4. On the mod's edit page, switch the description editor to its **source code** view (the `<>` /
   "Source code" button in the editor toolbar).
5. Select everything in the source view, delete it, paste the whole of `description.html`, and switch
   back to the normal view to check it. The spoilers save collapsed, which is intended.
6. Fill in the **Summary** (one of the options above) and the link fields, then **Save**.
7. Open the public page and check: the callout box at the top, every image showing, each spoiler
   opening, and the code blocks readable.

Notes for later edits:

- The editor strips `<script>`, `<style>` blocks, `<link>`, forms, `on*` attributes and most embeds.
  Keep styling inline, as the file does.
- **Do not add HTML comments** - the site turns them into visible text.
- The file is about 46 KB (most of it inline styling); the hard limit is 65,535 bytes. Keep
  additions modest.
- If the editor rewrites something on save, edit `description.html` here to match so the next paste
  does not undo it.
