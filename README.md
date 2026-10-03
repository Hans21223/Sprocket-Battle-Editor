# Sprocket Battle Editor

Make your own battles on [Sprocket](https://store.steampowered.com/app/1674170/Sprocket/)'s custom battle maps. Place
any of your designs (or the game's own tanks and AT guns) for either team, give them paths and targets, add a mission
with zones, mines, obstacles, rules and artillery, and film it with a keyframed, multi-camera cinematic. In any custom
battle you can also command every tank on the field, or take over one tank's driving, turret and gun.

For **Sprocket 0.2.55.5** with the [Sprocket Mod Loader](https://github.com/Hans21223/Sprocket-Mod-Loader)
(BepInEx 6 IL2CPP).

> **Pre-release.** Several parts haven't been tried in the game yet; see [Not tried in the game yet](#not-tried-in-the-game-yet).
> Please report what works and what doesn't in [Issues](../../issues).

## Install

With **Sprocket Mod Manager**: **Add mod**, choose the ZIP from [Releases](../../releases). By hand: copy the ZIP's
`BepInEx` folder into the Sprocket folder.

## Starting

- **Main menu → Battle Editor** (just under Custom Battle) opens the **Custom Battles** screen: the game's own Scenarios
  screen, listing your battles. Point at one to see its map, description, objectives and fail conditions.
  - **New custom battle**: pick a map, and the editor opens on it.
  - **Click a battle** for its actions: Play, Edit, Rename, Description, Clouds, Fog, Duplicate, Delete.
  - Starting goes through the game's Custom Battle screen behind a plain "Starting…" cover, filled in for you.
- **F9** in any custom battle opens the editor; **F10** opens the command view.

## The editor (F9)

The battle waits while you edit. **Play** restarts it with your tanks where you put them; **Esc** (with nothing else to
cancel) leaves the editor for the game's pause menu.

**Tanks**
- Click the ground to place the picked design for the picked team (drag to point it). Drag a tank to move it, its
  arrow's tip to turn it. Q / E turn it 15° (Shift: 1°), Delete removes it.
- Per tank: who drives it (you or the AI), **Path** (points it drives through, in order), **Target** (the tank it goes
  for, stopping to fire or firing on the move), and **Reserve** (kept off the map until a rule brings its team's
  reserves in). The path points show about when the tank gets to each.
- Your designs come from `Documents\My Games\Sprocket\Factions`; the game's own vehicles (its AT guns, scenario and
  historical tanks) are listed too, marked "(base game)".

**Mission**
- **Zones** (named circles), **AT mines** (the game's own mine), **hedgehogs** (break like the game's), **concrete
  blocks**, **AT guns** (the game's, placed as units) and an **erase** tool.
- **Rules**: *when* (after a time, a tank destroyed, a team wiped out, a team has lost N, a team or tank enters a zone,
  a team holds a zone) → *do* (a message, victory, defeat, an artillery barrage on a zone with a shell size from 75 to
  240 mm, a team's reserves arrive, a team or tank drives to a zone). Each rule fires once.

**Cinematic**
- **Cameras**: fly the editor's view where you want it and key it at the time cursor. A camera can follow a tank and
  always look at one; set its field of view; **cuts** say which camera is shown from when.
- **Timeline** along the bottom, as in video editing software: the ruler, the cuts, a track per camera and per keyed
  tank. Drag keys and cut edges; the wheel zooms, Shift + wheel scrolls; drag the **End** mark to set its length.
  **Preview** plays it over the frozen editor.
- **Tank keys** take a tank from its AI for a while: aim (turret and gun angles, a point, a tank, or *shoot at* a tank),
  drive (throttle and steering, or *drive to* a point), and fire. The panel shows how long the turret takes to come
  round and when a "drive to" gets there; the timeline draws the drive as a bar.
- It plays when the battle starts (and at a slower battle speed if you like); Esc stops it.

**Time estimates** use the game's own drivetrain maths on each design's engine, gearbox, tracks and mass (the same as
Quality of Life's speed panel), read from a spawned tank, so a design gets timed the first time it's in a battle. Each
real "drive to" in a cinematic corrects that design's times. Straight lines only: no way round obstacles or hills.

## The command view (F10)

The battle goes on while you command it. Select tanks of either team (left click, drag a box, Shift adds), right click
the ground to send them, right click a tank of the other team to attack it. **Space** pauses. **Take control** of one
tank: the arrow keys drive, the turret and gun follow the mouse (or the sliders), **G** fires.

## Keys

| Where | Key | What |
|---|---|---|
| Any custom battle | F9 / F10 | Editor / command view |
| Editor | Right drag, middle drag, wheel | Look, pan, zoom |
| Editor | W A S D, R / F (Shift: faster) | Fly |
| Editor | Tab, 1 / 2 | Next design, team |
| Editor | Q / E, Delete | Turn, remove the picked tank |
| Editor | Ctrl+S / Ctrl+L, T | Save / load, top view |
| Editor | Enter | Play |
| Editor | Esc | Cancel a tool, or leave for the pause menu |
| Command view | Space, Esc | Pause; drop the selection, or leave for the pause menu |
| Under force control | Arrow keys, mouse, G | Drive, aim, fire |

## Files

- Battles: `Documents\My Games\Sprocket\Battles\<name>.json` (plain JSON, easy to share).
- Worked-out drive and turret times per design: `BepInEx\config\SprocketBattles-travel.json`.
- What the mod did, for bug reports: `BepInEx\SprocketBattles-trace.log`.

## Not tried in the game yet

The menu, editor, timeline, cinematics, command view and artillery have been used in the game. These haven't, or only
partly:

- Mission: mines, obstacles, reserves and most rules; the artillery's shell sizes.
- The game's AT guns placed as units in a custom battle.
- Force control's steering direction; the cinematic tank keys' aim at and shoot at.
- Clouds / Fog from the menu, renaming and descriptions there.
- The turret turn times (the turret's speed unit is inferred) and the drive corrections.

## Building

```text
dotnet build SprocketBattles -c Release -p:GameDir="<your Sprocket folder>"
dotnet run --project SprocketBattles.Tests -c Release
```

The game folder needs the Sprocket Mod Loader, started once (it makes `BepInEx\interop`). The tests (the battle file
format and the drivetrain maths) run without the game. New to Sprocket modding? See
[Making Mods for Sprocket](docs/Making-Mods-for-Sprocket.pdf).

## License

[MIT](LICENSE).
