# Sprocket Battle Editor

Make your own battles on [Sprocket](https://store.steampowered.com/app/1674170/Sprocket/)'s custom battle maps. Place
any of your designs (or the game's own tanks and AT guns) for either team, give them paths and targets, add a mission
with zones, mines, obstacles, rules and artillery, and film it with a keyframed, multi-camera cinematic. In any custom
battle you can also command every tank on the field, or take over one tank's driving, turret and gun.

For **Sprocket 0.2.56.0** with the [Sprocket Mod Loader](https://github.com/Hans21223/Sprocket-Mod-Loader)
(BepInEx 6 IL2CPP).

The 0.2.56.0 builds use newly generated game interop. All 29 Harmony patch targets, the native scene-loader ABI,
23 game type contracts and the 22 offline regression suites pass. In-game testing remains pending.

**0.17.30 includes Battle Editor 0.17.30 and Map Framework 0.4.1.** Map Framework makes Ambush,
The Crossroad, Silent Border, Sandbox and Sandbox (Low performance) available to Custom Battle and Battle Editor.
It repairs Ambush's team setup and builds the missing custom-battle setups on the other maps. The Sandbox thumbnails
are included beside the Map Framework DLL.

Editor Save and Play use the tank positions you place. Random spawn settings belong to the quick-battle and Gauntlet launchers.

> **Alpha.** Several parts haven't been tried in the game yet; see [Not tried in the game yet](#not-tried-in-the-game-yet).
> Please report what works and what doesn't in [Issues](../../issues).

## Install

With **Sprocket Mod Manager**: **Add mod**, choose the ZIP from [Releases](../../releases). By hand: copy the ZIP's
`BepInEx` folder into the Sprocket folder.

Choose **Sprocket-Battle-Editor-and-Map-Framework-0.17.30.zip** for both plugins. Separate
**Sprocket-Battle-Editor-0.17.30.zip** and **Sprocket-Map-Framework-0.4.1.zip** downloads are also available; the loader is installed
separately. When updating, replace Battle Editor and remove any older extra copy of `SprocketMaps.dll` before copying
the included `BepInEx\plugins\SprocketMaps` folder. Keep the two Sandbox PNGs beside that DLL.

Custom map files go in `BepInEx\plugins\SprocketMaps\CustomMaps`. Use a Windows scene bundle built for Unity
6000.3.21f1, containing scenery, solid ground and readable collision meshes. It appears in the ordinary map picker.
Map Framework supplies the game's normal designer, player and battle controls, adapts the custom scenery's materials
and builds tank navigation. It checks spawn ground before starting. Custom environment bundles cannot contain
scripts or replace native game controllers. A file that cannot start reports the problem and returns to the menu.
The Map Framework ZIP includes the plugin, required compression library, Sandbox thumbnails and install guides.
Third-party city and FNAF scenery used in local testing is excluded from public downloads. Import your own licensed
map sources, or add a compatible scene bundle to `CustomMaps`. Existing local maps remain usable.
See [Map asset provenance](Release/Map-Asset-Provenance.txt) for the local fixture origins and distribution scope.
Map Framework **0.4.1** also imports raw **OBJ, FBX and `.unitypackage`** maps automatically. Put a source archive
(`.zip`, `.7z` or `.unitypackage`) directly in `CustomMaps`, or put each model and its textures in a separate
subfolder there. Nested ZIP/7z archives are unpacked. The matching **Unity 6000.3.21f1 editor must be installed and
licensed**; it converts a private copy in the background, so no manual Unity export is needed. Standard Unity Hub
and drive `Unity` installations are detected. Other locations can be set in `BepInEx/config/local.sprocket.maps.cfg`
under **Import / Unity editor path**; **Additional source folder** accepts another map folder.
The import notice reports progress. Reopen the map picker after it finishes. Unchanged sources use their verified
cached conversion even without Unity installed; changed sources require a restart if that map was already registered.
Selecting a loose OBJ or FBX imports that model with its texture/material sidecars. Unity packages use the largest
included scene, or the largest model/prefab if they contain no scene. Keep all referenced textures with the source.
Scripts and executable package contents are excluded; custom scripted gameplay and shaders are not imported.
Static meshes, textures, authored transforms and conventional material colours are preserved, with materials
adapted to the game's lighting. The importer builds collision/navigation and offers only checked spawn capacity.
Maps need solid ground at least 100 metres across both axes and two connected tank areas at least 100 metres apart.
Conversion failures leave built-in maps available and include a diagnostic log path. The source files stay unchanged.
The standalone **Sprocket-Map-Framework-0.4.1.zip** includes the required `SharpCompress.dll`; keep it beside
`SprocketMaps.dll`. Gameplay validation remains separate from the offline conversion checks.
An optional JSON file beside a bundle declares its checked tank capacity: for `city.bundle`, use `city.json`
containing `{"schemaVersion":1,"spawnCount":4,"displayName":"City"}` (1 to 16 tanks per side; displayName is optional). The map picker and spawn formations use
that capacity. Include authored transforms named `Sprocket Spawn Area A` and `Sprocket Spawn Area B` at the centers
of two clear, connected spawn areas at least 100 metres apart. Each offered slot must support an 8 by 8 metre tank
footprint and the native tank navigation agent; loading checks these positions before starting.

## Starting

- **Main menu → Battle Editor** (just under Custom Battle) opens the **Battle Editor** screen: the game's own Scenarios
  screen, listing your battles (it scrolls when there are many). The **Editor** and **Playing** tabs stay above the
  list. Point at a battle to see its map, description, objectives and fail conditions.
  - **Editor → New battle**: pick a map, and the editor opens on it.
  - **Playing → Quick battle**: choose the map (or any), 1 to 8 tanks a side, **Player faction**, **Enemy faction**
    and the era (or any), then Start: each tank is a random pick from its side's faction, starting where the game
    spawns it. Select **Free for all** for 2 to 8 tanks fighting individually. **Random spawn points** is on by default;
    click **Minimum spacing (m)** and **Maximum spacing (m)** to set the distance to each tank's nearest opponent,
    from 16 to 2,000 metres. Turn random spawns off to use the map's normal placement. Not saved.
    Click a faction selector to open its list. Choose a named faction, all your factions, base-game tanks or all factions.
    If a faction has no usable tanks in the chosen era, Start explains which side needs a different selection.
  - **Playing → Gauntlet**: choose a starting budget, 2 to 30 rounds, a map, **Enemy faction** and an era.
    Every opponent round uses that faction; player tank selection stays in the full vehicle designer.
    **Choose tank & start** opens the full vehicle designer. Buy one starting tank from your funds, then face
    increasingly numerous and expensive opponents. Enemy count stays within the map's native capacity and at most sixteen.
    Between rounds, return to the full designer to repair or refit within your remaining funds. See [Gauntlet funds](#gauntlet-funds).
  - **Editor → click a battle** for Edit battle, Rename, Description, Objectives, Fails if, Clouds, Fog, Duplicate,
    Share and Delete. Open **Edit battle → Mission → Player tank limits** to set **Maximum tanks**, **Total tank
    budget**, **Maximum cost per tank**, player positions and individual **Allowed eras**. Click a numeric value,
    type the amount and press Enter; Esc cancels. `0` means no extra numeric limit; the count cannot exceed the
    available player positions. **Players choose all Team 1 tanks** marks that team's positions for selection.
    **Add player position** adds another at a native map spawn (place a Team 1 tank in the Tanks tab first).
    The limit panel scrolls and keeps the mission's Rules panel visible. Save keeps these settings with the battle.
    **Objectives** and **Fails if** are what the menu shows for it (several split by `;`); left empty,
    they come from the mission's victory and defeat rules.
  - **Playing → click a battle → Choose tanks & play** opens the full normal vehicle designer on this battle's map.
    Use its **Load**
    button to choose a tank, then edit it with the normal tools. The **Scenario** display shows the battle's tank
    count, budget, cost per tank and allowed eras. Use the top **Play** arrow or **Space** to start the battle;
    all limits are checked before loading. For battles allowing several tanks, **Add tank**, **Previous/Next**
    and **Remove** manage your lineup and keep the other tanks' edits. Saving to your personal blueprints is
    optional: starting or switching tanks records a private battle draft. **Esc → Main menu** leaves preparation.
    **Play with battle tanks** uses the authored lineup. Choosing tanks does not change the author's saved battle.
  - **Editor → Share** writes the battle as one `.zip` in `My Games\Sprocket\Battles\Shared` (and opens that folder): the
    battle, every design of yours it uses, and their custom paint and decal pictures. Send that file.
  - **Import a shared battle** lists the shared battles in that folder and in Downloads; click one to put it in. Its
    designs go into a faction of their own, "Shared battles" (one you already have from before is used again).
  - Starting goes through the game's Custom Battle screen behind a plain "Starting…" cover, filled in for you.
- Open the map editor from **Editor → Edit battle**. **F10** opens the command view during play.
  **F9** toggles back to the same editor after editor Play, F9 or **Esc**, including from its pause menu.
  It does not enter editing from an ordinary battle that wasn't paused from the editor.

## The editor

The battle waits while you edit. **Play** restarts it with your tanks where you put them; **Esc** (with nothing else to
cancel) leaves the editor for the game's pause menu.

**Tanks**
- **Mode** switches between Team battle and **Free for all**. Save keeps the mode; it can also be changed in
  a saved battle's menu. Free-for-All supports 2 to 8 tanks in total, including reserves. Every tank is an opponent;
  the last mobile tank wins. Keep a starting tank in each blue/red spawn group for native loading. These groups
  still organize player positions and mission rules, but do not create allies in this mode.
  **Random spawn points**, **Minimum spacing (m)** and **Maximum spacing (m)** in Quick battle or the saved battle's
  Editor menu control random placement. The range describes the distance to each tank's nearest opponent.
  Placement is prepared before the game constructs tanks, without teleporting live track physics.
- **Hold position** cancels that tank's movement when playing while leaving its aiming and firing active.
  Team-battle tanks with no movement orders hold automatically; finished paths return to holding unless an attack is active.
  Free-for-All AI seeks opponents and attacks or approaches them automatically. Explicit Force stop, player control,
  paths and manual move/attack orders take priority over this automatic behavior.
- Click the ground to place the picked design for the picked team (drag to point it). Drag a tank to move it, its
  arrow's tip to turn it. Q / E turn it 15° (Shift: 1°), Delete removes it.
- A placed tank shows as its design's own shape, in its team's colour, once a tank of that design has been in a
  battle (kept in `BepInEx\cache\SprocketBattles-shapes` until the design changes); a box until then.
- **Player picks it** (Team 1's tanks): the player brings their own design to this tank when the battle is played
  from the menu. It keeps its place, path, target and part in the mission; the design placed is a stand-in. A cyan
  disc marks it. Set the shared limits in **Mission → Player tank limits**: count, total budget, cost per tank and
  individual eras, including custom ones. The count is capped by marked positions; use `0` for all positions.
- Per tank: who drives it (you or the AI), **Path** (points it drives through, in order), **Target** (the tank it goes
  for, stopping to fire or firing on the move), and **Reserve** (kept off the map until a rule brings its team's
  reserves in). The path points show about when the tank gets to each.
- Your designs come from `Documents\My Games\Sprocket\Factions`; the game's own vehicles (its AT guns, scenario and
  historical tanks) are listed too, marked "(base game)". **Faction** (< and >, or click its name) shows one faction's
  designs, the game's own ("Base game"), or all of them. **Era** does the same by era: the game's own and any custom
  era files in `Sprocket_Data\StreamingAssets\Eras`, a design's era by the date it was made (or the era an older
  design names).
- Playing a battle with player choices opens the game's full vehicle designer on the chosen battle map. Load and edit your tanks there,
  then use its Play arrow or Space. Your choices replace the marked positions and keep their orders and mission
  roles. The native Custom Battle screen is used only behind the loading cover to start the configured battle.
- **Load saved** (Ctrl+L) lists the battles saved on this map, newest first; click one to load it, Esc to close.

**Mission**
- **Player tank limits** opens a scrollable fold in the left panel. Set player choices and limits here, then Save.
  Close the fold to return to placement tools and zones; the Rules panel stays visible.
- **Zones** (named circles), **AT mines** (the game's own mine), **hedgehogs** (break like the game's), **concrete
  blocks**, **AT guns** (the game's, placed as units) and an **erase** tool.
- **Rules**: *when* (after a time, a tank destroyed, a team wiped out, a team has lost N, a team or tank enters a zone,
  a team holds a zone) → *do* (a message, victory, defeat, an artillery barrage on a zone with a shell size from 75 to
  240 mm, a team's reserves arrive, a team or tank drives to a zone). Each rule fires once.

**Cinematic**
- **Record battle** in **Cinematic setup** starts the placed battle and records its tank movement, visible part poses and a camera key
  every two seconds. **F9** or **Stop & edit** saves the recording and opens it in the cinematic editor;
  **Stop & save** keeps playing. Recording stops and saves on scene exit or at its duration/storage limit.
- During a running Battle Editor battle, **F10 → Record current battle** records from the current instant without
  restarting, respawning tanks or rerunning the AI. Replay stores movement and models from the actual battle;
  it keeps editable 3D data rather than fixed video frames so cameras can be changed afterward.
- **Replays — Edit** opens the recorded replay list, with names, maps and durations. Choose **Edit** to open its
  map in the separate **Replay Editor**, scrub recorded movement and edit cameras/cuts. **Preview** and **Play replay** show recorded visual actors over
  a frozen map. They do not rerun the battle AI or move live vehicle physics.
  **Save replay edits** writes a separate editable cinematic battle; the original recording is unchanged.
  **Load saved replay edits** opens those edits. **Back to battle** restores the previous battle tab;
  when opened on another map, **Main menu** leaves the replay workspace. Battle setup controls are hidden here.
  Recordings are in `Documents\My Games\Sprocket\Battles\Replays`.
  Tanks use lit models with captured paint textures, normals, UVs and separate material slots. Capture selects one
  model per part, excludes shadow/LOD duplicates and reads GPU-only meshes. Shared geometry and a reserved allowance
  for each design prevent one design from consuming every tank's geometry budget. Oversized models use a lower LOD
  when available. Recordings are bounded to 20 minutes, 150,000 pose samples, 1,000,000 shared vertices and 16 MB of
  paint textures. New recordings also capture the game's instanced track belts and complete native paint shader
  settings, including tint, paint texture, mapping and weathering properties. Sound events and engine loops are
  recorded with position, volume, pitch and sample timing; Preview/Play seeks and stops them with the timeline.
  Readable sound clips are embedded as compressed PCM; streamed/compressed clips resolve against exact loaded game
  assets. Missing clips are reported. Particles, projectiles and projected decals are not recorded.
  Record again for tracks, native paint and sounds that were absent from older recordings.
- **Cameras**: fly the editor's view where you want it and key it at the time cursor. A camera can follow a tank and
  always look at one; set its field of view; **cuts** say which camera is shown from when.
- Click a camera key's model on the map and drag to move it. Hold **Shift** while dragging to adjust height.
  **Ctrl-drag** rotates it; **Yaw / Pitch / Roll ±5°** buttons adjust the selected key. Manual rotation turns off
  automatic Look at, and keeps its position, time, zoom and followed-tank frame unchanged.
  Selection keeps the editing view in place. New cameras start with a key at the current view. **Height ±1 m**,
  **Use view** and **FOV ±5** change the selected key; **Use view** copies the editor's position, direction and zoom.
- **Camera view** shows a live view of the selected camera without camera/path gizmos. It follows timeline scrubbing,
  movement, zoom and follow/look-at settings. **View here** moves the editor into that camera when requested.
  F9 stops replay Preview/Play and returns to editing. F9 also returns from a live battle started with editor Play;
  F9 can then toggle back to the same editor, including after Esc opens the pause menu.
- **Timeline** along the bottom, as in video editing software: the ruler, the cuts, a track per camera and per keyed
  tank. Drag keys and cut edges; the wheel zooms, Shift + wheel scrolls; drag the **End** mark to set its length.
  **Preview** plays it over the frozen editor.
  The timeline draws before optional camera previews, so a preview error cannot hide it.
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
**Force stop** cancels selected AI tanks' movement and brakes them without blocking their guns. A new move or
attack order releases the stop; **Release to AI** restores autonomous behavior. Quick battles keep autonomous
movement until commanded. In Free-for-All, right click any other tank to attack, even one in the same spawn group.

## Gauntlet funds

Your first tank's full price is deducted from your starting budget. Completing a round awards **10,000 + 2,500 ×
the completed round number**, independent of starting funds. Before the next round, the full native vehicle designer opens again.
Repairs and refits must fit your remaining funds; the quote is checked before a round starts.

Era unlock prices are based on era progression, tank prices, mass and dates, including custom era fees.
Starting funds do not change these prices. Advancing pays the difference between the current unlocked tier and
the requested tier; already unlocked eras are free. Older saved runs refresh their era prices while preserving
earned credits, repair bills and unlocked eras.

- **Repair** is mandatory: **30% of the previous tank's price × its component damage fraction**. Damage is read from
  the native component health pools. If a complete health reading is unavailable, the quote assumes full damage.
- **Upgrade** charges any positive increase in tank price. A changed design also costs **5% of the resulting tank's
  price** for refit labour. Saving an unchanged copy or changing its name does not count as a refit.
- Downgrading gives no refund. A round's charge and reward can each happen only once.

Complete your selected 2 to 30 rounds to win. Losing your tank ends the run.

## Keys

| Where | Key | What |
|---|---|---|
| Any custom battle | F10 | Command view |
| Editor | F9 | Stop replay preview; toggle the same editor after editor Play, F9 or Esc |
| Editor | Right drag, middle drag, wheel | Look, pan, zoom |
| Editor | W A S D, R / F (Shift: faster) | Fly |
| Editor | Tab, 1 / 2 | Next design, team |
| Editor | Q / E, Delete | Turn, remove the picked tank |
| Editor | Ctrl+S / Ctrl+L, T | Save / the saved battles on this map, top view |
| Editor | Enter | Play |
| Editor | Esc | Cancel a tool, or leave for the pause menu |
| Command view | Space, Esc | Pause; drop the selection, or leave for the pause menu |
| Under force control | Arrow keys, mouse, G | Drive, aim, fire |

## Files

- Battles: `Documents\My Games\Sprocket\Battles\<name>.json` (plain JSON, easy to share).
- Private tank drafts: `Documents\My Games\Sprocket\Battles\Drafts`. Keep these when saving a played battle;
  sharing a battle includes any drafts it uses.
- Worked-out drive and turret times per design: `BepInEx\config\SprocketBattles-travel.json`.
- Each design's shape for the markers: `BepInEx\cache\SprocketBattles-shapes`.
- What the mod did, for bug reports: `BepInEx\SprocketBattles-trace.log`.

## Not tried in the game yet

The menu, editor, timeline, cinematics, command view and artillery have been used in the game. These haven't, or only
partly:

- Mission: mines, obstacles, reserves and most rules; the artillery's shell sizes.
- The game's AT guns placed as units in a custom battle.
- Force control's steering direction; the cinematic tank keys' aim at and shoot at.
- Clouds / Fog from the menu, renaming and descriptions there.
- The new Editor/Playing tabs and limits, the direct native vehicle designer, Scenario display and Play handoff.
- Native spawning at authored positions before vehicle construction, without relocating live track physics.
- Random Free-for-All placement and autonomous attack behavior; Gauntlet round transitions, native designer refits and repair quotes.
- Sharing and importing battles, the faction and era pickers, the saved battles list, objectives and fail
  conditions from the menu, scrolling a long list of battles.
- The turret turn times (the turret's speed unit is inferred) and the drive corrections.
- The tanks' shapes in the editor.

## Building

```text
dotnet build SprocketBattles -c Release -p:GameDir="<your Sprocket folder>"
dotnet build SprocketMaps -c Release -p:GameDir="<your Sprocket folder>"
dotnet run --project SprocketBattles.Tests -c Release
powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 -Rebuild -GameDir "<your Sprocket folder>"
```

The game folder needs the Sprocket Mod Loader, started once (it makes `BepInEx\interop`). The tests (the battle file
format, drivetrain maths, sharing, spawn-clearance planner and Gauntlet ledger/progression) run without the game. New to Sprocket modding? See
[Making Mods for Sprocket](docs/Making-Mods-for-Sprocket.pdf).

The `SprocketMaps` project is included here so the source download contains both mods. Its starting source is Map
Framework 0.3.1, revision `9d30b32`; the current build adds terrain-aware spawn staging and supplies authored positions
to the native spawner before vehicle construction. The packaging script rebuilds against the specified GameDir,
checks assembly versions, includes the required SharpCompress library and install assets, and writes a SHA256
checksum in `artifacts`. Builds and offline battle, drivetrain, sharing and spawn-clearance tests pass; this bundled release has
not been run in a new game session.

Already verified build outputs can be supplied with `-BattleBuildDirectory` and `-MapBuildDirectory` instead of
`-Rebuild`. Old `bin/Release` files are never selected automatically. `-Package All` also creates separate Battle Editor
and Map Framework ZIPs, and `-ArchiveSuffix local-sprocket-0.2.56.0` labels local compatibility builds.
Public packages exclude third-party scenery, installed maps, user sources and converter caches. Local fixtures can
be included only with `-IncludeLocalMaps` and a `local-` archive suffix; those packages are not public release assets.

## License

[MIT](LICENSE).
