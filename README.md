# Nivalis Menu Planner

A [Nivalis Nights](https://www.nexusmods.com/nivalisnights) mod that plans venue menus —
and, in later versions, lets your hired managers run them at the level their skill deserves.

At day start (08:00) it looks at what you've unlocked, what's in venue storage, what vendors
charge today, and your district's customer mix, then suggests a menu per venue: dishes that
clear the profit bar, a shopping list sized to expected demand, and a payroll escrow so
suggested spending never touches the wages your staff is owed.

**Status: v0.1 — advisory.** The planner suggests; you apply menus by hand in the venue
screen. Nothing is automatic yet, and nothing will be without a manager doing their job —
see the design philosophy below.

## Features

- **Profit-bar menu planning** — every dish must be expected to earn; preferences for a
  beverage, entrée and dessert tip close calls but never rescue a loser
- **Day-start rotation** — plans refresh at 08:00 with churn stickiness, so menus change
  when the world changes, not on noise
- **Pantry-aware shopping lists** — venue stock counts at a discount (use it or lose it),
  and craft-chain ingredients are priced through their recipes (Beer Soup's Beer is brewed
  from Wheat, so the list asks for Wheat)
- **Payroll escrow** — the full shift's wages are reserved before any suggested spending
  (live data from the venue's staff records)
- **In-game window** (default F8), a pause-menu Mods page, and a `plan` dev command
- Menu history persists with your save (per-save JSON, via the ModKit)

## Design philosophy: no cheats, just staff work

Anything this mod does to the world should be a hired manager doing their job as well as
their managerial level allows. The player with no manager is the grade-0 manager. The mod
never acts on its own.

- **No manager:** the planner suggests; you apply menus and prices by hand (today)
- **Grade 1–2 manager:** proposals you approve (pending ModKit APIs)
- **Grade 3+ manager:** daily auto-rotation under your rules (pending ModKit APIs)

The manager-progression half is gated on a few read/write hooks requested from the
Nivalis ModKit. Until then, everything is advisory.

## Requirements

- Nivalis Nights (tested on 1.0 patch 4)
- BepInEx 6 bleeding-edge build 788 (IL2CPP) — the community preconfigured pack works
- Nivalis ModKit 0.6.2 or newer

## Install

1. Install BepInEx 6 be.788 IL2CPP into the game folder
2. Install the Nivalis ModKit into `BepInEx\plugins\`
3. Copy `NivalisMenuPlanner.Plugin.dll`, `NivalisMenuPlanner.Core.dll` and `dishdata.csv`
   from a release into `BepInEx\plugins\NivalisMenuPlanner\`
4. Launch; once in gameplay, press F8 (rebindable via the pause menu → Mods)

## Building from source

A standard `dotnet build` — except `libs\`, which is deliberately not in this repo: it holds
a local BepInEx install (loader plus auto-generated interop assemblies; the interop set is
derived from the game and shouldn't be redistributed). Copy your game's `BepInEx\` folder
to `libs\BepInEx\` — including `plugins\NivalisModKit.dll` and its `NivalisModKit.xml` —
then build the solution.

`dishdata.csv` regenerates from the ModKit's `content dump` via `tools\make_dishdata.ps1` —
re-run it after every game patch.

## Honest data notes

Dish price caps and per-customer-group satisfaction aren't in the game's data dumps, so the
shipped `dishdata.csv` estimates them (input cost × type factor; tag heuristics). v0.2
replaces estimates with harvested reality (NPC menu prices, real sales history). The
payroll math is verified in-game against live wages.

## Credits

- Nivalis ModKit by BGASM — this mod is built entirely on it, and wouldn't exist without it
- BepInEx — the loader that makes any of this possible
- ION LANDS — for a game about running restaurants that somehow still needed a restaurant mod

MIT licensed. Nivalis Nights is ION LANDS' game; this is a fan mod with no affiliation.