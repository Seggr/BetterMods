# Third-party notices

## What the BetterMods licence covers

BetterMods is licensed under the **Zero-Clause BSD licence** (SPDX `0BSD`) - see [`LICENSE`](LICENSE).
You may use, copy, modify and distribute it for any purpose, with or without a fee, and you do not
have to credit anyone.

That licence covers **BetterMods' own work only**: its source code (the `BetterMods` mod and the
`BetterModsHook` startup hook) and its documentation.

It grants **no rights at all** in anything BetterMods links to, runs inside, talks to or downloads.
Those things belong to their own owners and come with their own licences and terms. If you use,
modify or redistribute BetterMods - as it is or changed - you must also follow the licences and terms
of the components listed below. Nothing in BetterMods' licence overrides, extends or replaces them.

## What BetterMods redistributes

**None of the components below.** BetterMods compiles against them but does not ship them:

- Every library reference in `BetterMods.csproj` (`VintagestoryAPI`, `VintagestoryLib`, `0Harmony`,
  `Newtonsoft.Json`, `cairo-sharp`) is marked `<Private>false</Private>`, so none of them is copied
  into the build output. They are loaded at run time from the player's own Vintage Story install.
- `BetterModsHook` references only the .NET framework - no game or third-party assembly. It is
  BetterMods' own code. It is embedded inside `BetterMods.dll` and written out by the mod itself.
- A release zip holds exactly the build output (`modinfo.json`, `BetterMods.dll`,
  `BetterMods.pdb`, `BetterMods.deps.json`) plus `LICENSE` and this file. All of that is BetterMods'
  own work.

## Components BetterMods links to or works with

Each licence below was checked against the source linked beside it (November 2026). Licences can
change; the linked source is authoritative, not this summary.

| Component | Owner | Licence / terms | Verified against |
|---|---|---|---|
| **Vintage Story** - the game, its program files and all of its assets (art, sounds, models, text, translations) | Anego Studios SIA | Proprietary. Use is governed by Anego Studios' own terms. No separate public EULA was found on vintagestory.at; the site's Terms of Service apply, together with any terms that come with the game. | <https://www.vintagestory.at/tos.html> |
| **VintagestoryAPI** (`VintagestoryAPI.dll`) | Anego Studios | Proprietary, "All rights reserved". The published source may be read, learned from and used as a basis for Vintage Story mods; it may not be sold, or copied, compiled or distributed in unmodified form. | <https://github.com/anegostudios/vsapi/blob/master/license.txt> |
| **VintagestoryLib** (`VintagestoryLib.dll`) | Anego Studios | Proprietary, part of the game. No public source repository or separate licence was found, so it falls under the game's terms above. | <https://www.vintagestory.at/tos.html> |
| **Official content mods** - `VSSurvivalMod`, `VSEssentials`, `VSCreativeMod`. BetterMods reads their mod information, treats them as core mods and asks for confirmation before a profile switches one off. | Anego Studios | Proprietary, "All rights reserved" - the same terms as VintagestoryAPI. | <https://github.com/anegostudios/vssurvivalmod/blob/master/license.txt><br><https://github.com/anegostudios/vsessentialsmod/blob/master/license.txt><br><https://github.com/anegostudios/vscreativemod/blob/master/license.txt> |
| **Harmony** (`0Harmony.dll`, shipped with Vintage Story - 2.4.2 at the time of writing) | Andreas Pardeike | MIT License | <https://github.com/pardeike/Harmony/blob/master/LICENSE> |
| **Newtonsoft.Json** (Json.NET, shipped with Vintage Story - 13.0.4 at the time of writing) | James Newton-King | MIT License | <https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md> |
| **cairo-sharp** (`cairo-sharp.dll`, shipped with Vintage Story) | Not stated in the binary | **Not verified.** The copy shipped with the game carries no product, company or copyright information, so its origin and licence could not be confirmed. See the component's own licence, and the terms that come with Vintage Story, which ships it. | `Lib\cairo-sharp.dll` in the Vintage Story install; <https://www.vintagestory.at/tos.html> |
| **.NET runtime and base class libraries** (`net10.0`) - BetterMods and BetterModsHook run on them | .NET Foundation and contributors | MIT License | <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |
| **Vintage Story mod DB** (`mods.vintagestory.at`) - the update checker and Fix dependencies query it and download mods from it; "Open mod page" opens its pages | Anego Studios SIA (the service); each mod's own authors (the mods) | Use of the site and service is governed by Anego Studios' Terms of Service. Every mod BetterMods downloads stays under **its own authors' licence and terms**. BetterMods' licence does not apply to those mods, and BetterMods grants no rights in them. | <https://www.vintagestory.at/tos.html>; each mod's own page and files |

## Development material that is not part of BetterMods

- Where code comments name or quote parts of Vintage Story's code, that code remains Anego Studios'
  property; the 0BSD licence covers only BetterMods' own writing around it.
- Screenshots in this repository's documentation show Vintage Story's interface and artwork. What they
  depict remains Anego Studios' property (and, for third-party mods shown, their authors').
