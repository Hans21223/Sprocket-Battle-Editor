Sprocket Battle Editor 0.17.31 + Map Framework 0.4.1 - for Sprocket 0.2.56.0 (Alpha)
https://github.com/Hans21223/Sprocket-Battle-Editor

Rebuilt against fresh 0.2.56.0 game interop. Offline regression, patch-target and native ABI checks pass.
In-game testing remains pending.
0.17.31: with the optional Sprocket Mod API (github.com/furryaxw/SprocketModAPI) installed, F9 and F10 can be
rebound in its keybinding window and Battle Editor is listed in its Mod menu. Without the API: no change.

INSTALL
Needs the Sprocket Mod Loader (BepInEx 6 IL2CPP). Add the zip in Sprocket Mod Manager, or copy its BepInEx folder into
the Sprocket folder.
Both plugins are included. Map Framework and its two Sandbox PNGs live together in BepInEx\plugins\SprocketMaps.
Remove any older extra copy of SprocketMaps.dll when updating, so only one copy is installed. Install the loader
separately; this package contains no loader or game files.

MAP FRAMEWORK
Makes Ambush, The Crossroad, Silent Border, Sandbox and Sandbox (Low performance) available in Custom Battle and
Battle Editor. Repairs Ambush's team setup and builds custom-battle setups on the other maps. Keep the Sandbox
pictures beside SprocketMaps.dll. Spawn staging now samples nearby terrain and keeps clearance above slopes.

START
- Main menu > Battle Editor (under Custom Battle): your battles on the game's Scenarios screen, with fixed Editor
  and Playing tabs above the scrolling list.
- Editor: New battle picks a map and opens the editor. Click a saved battle for Edit battle, Rename, Description,
  Objectives, Fails if, Clouds, Fog, Duplicate, Share and Delete. Open Edit battle > Mission > Player tank limits
  for Maximum tanks, Total tank budget, Maximum cost per tank, Allowed eras and player positions. Numeric 0 means
  no extra limit; count cannot exceed player positions. Click a numeric value, type, Enter applies; Esc cancels.
  Set Team 1 player choices and add positions here. The fold scrolls; Save keeps these settings with the battle.
- Playing: Quick battle starts random tanks with nothing to set up. Click a saved battle for Choose tanks & play
  or Play with battle tanks. Choosing tanks leaves the author's saved battle unchanged.
- Quick battle: Player faction and Enemy faction choose each side independently. Click to expand the faction list:
  a named faction, all your factions, base-game tanks or all factions. Era still filters eligible tanks.
  Start explains which side has no usable designs; it never fills that side from another faction.
- Playing > Gauntlet: set a starting budget, 2 to 30 rounds, map, Enemy faction and era. Every round draws opponents
  from the selected faction. Choose tank & start opens
  the full vehicle designer. Buy a starting tank, then fight increasingly numerous and expensive opponents.
  Opponent count is capped by the map's native capacity and at most sixteen. The full designer opens between rounds
  for repairs and refits within your remaining funds; completing rounds earns rewards. See GAUNTLET FUNDS below.
- Choose tanks & play opens the full normal vehicle designer on the selected battle map. Use the native Load button to choose a tank and
  edit it with the usual tools. Scenario shows the count, budget, per-tank cost and era limits. The top Play arrow
  or Space starts the battle after checking every limit. Add tank, Previous/Next and Remove manage larger lineups.
  Saving to personal blueprints is optional; starting or switching tanks keeps a private battle draft.
  Remove can clear the final selected tank; it remains as a preview until Add tank includes it again.
  The selected count and maximum are shown separately. Play requires at least one selected tank.
  Esc > Main menu leaves preparation. The author's saved battle stays unchanged.
- Share writes the battle as one zip in Documents\My Games\Sprocket\Battles\Shared, with the designs of yours it uses
  and their paint and decal pictures. Import a shared battle puts one in from there or from Downloads; its designs
  go into a faction of their own, "Shared battles".
- A battle with tanks marked "Player picks it" opens the full vehicle designer to prepare Team 1. The native
  Custom Battle screen is used only behind the loading cover to start the configured battle.
- Open the map editor from Editor > Edit battle. F10 opens the command view during play. F9 toggles back to the same
  editor after editor Play, F9 or Esc, including from its pause menu. Other battles do not enable F9 editing.

THE EDITOR
- Mode switches Team battle / Free for all. Save keeps it; saved battle settings and Quick battle offer it too.
  Free for all supports 2 to 8 tanks including reserves: every tank is an enemy, last mobile tank wins.
  Blue/red stay placement and rule groups; a starting tank is needed in each group for native loading.
- Quick battle and saved battle settings offer Random spawn points and editable Minimum/Maximum spacing (m),
  from 16 to 2,000 metres. This is the distance to each tank's nearest opponent. Random placement is prepared
  before native vehicle construction; turning it off uses saved placement or the map's normal spawn points.
- Hold position cancels movement while leaving aiming/firing active. Authored team-battle idle tanks hold automatically;
  finished movement orders return to holding. Free-for-All AI picks opponents and attacks or approaches them.
  Explicit Force stop, player driving, paths and manual move/attack orders take priority over automatic FFA actions.
  Quick team battles retain autonomous movement unless explicitly stopped.
- Tanks: pick a faction and an era (the game's eras, custom ones too), then a design; click the ground to place it
  for a team (drag to point it), drag to move, Q / E to turn, Delete to remove. A tank shows as its design's shape
  once that design has been in a battle. Per tank: who drives it, a path, a main target, reserve, and whether the player picks its design (with limits:
  tank count, budget, cost a tank, eras). Play restarts the
  battle with your tanks. Load saved lists the battles saved on the map.
- Mission: Player tank limits in a scrollable fold; zones, AT mines, hedgehogs, concrete blocks, the game's AT guns, and rules ("when this happens, do that":
  messages, victory, defeat, artillery with a shell size, reserves, drive to a zone).
- Cinematic: cameras with keys (follow and look at a tank), cuts, a timeline with an End mark, Preview, and tank keys
  (aim, shoot at, drive, drive to, fire) with estimated turret and drive times.
- Cinematic setup > Record battle starts the placed battle. F9 / Stop & edit saves and opens the recording; Stop & save
  keeps playing. Replays > Edit lists recordings by name, map and duration and opens them for camera/cut editing.
  F10 > Record current battle starts recording a running Battle Editor battle now, without restarting it.
  The separate Replay Editor shows recorded actors on a frozen map with Preview / Play replay. Save replay edits
  writes a separate cinematic battle; Load saved replay edits opens it. Back to battle restores the prior battle
  tab, or Main menu leaves a replay opened on another map. Normal battle controls are hidden in the Replay Editor.
  The bottom timeline draws first; errors in the optional camera preview cannot hide it.
  Original recordings stay in Documents\My Games\Sprocket\Battles\Replays. Movement and visible part poses are
  captured with lit materials, paint textures, normals, UVs and material slots. One model per part excludes shadow
  and LOD duplicates; GPU-only meshes are read and geometry is shared across identical tanks. Each design has a
  reserved allowance, with lower LODs used when available. New recordings include instanced track belts and native
  paint shader settings, including tint, mapping and weathering. Engine loops and sound effects retain position,
  volume, pitch and timing. Preview/Play seeks and stops sound with the timeline. Readable clips are embedded;
  compressed/streamed clips use exact loaded game assets. Missing clips are reported.
  Particles, projectiles and projected decals are not recorded. Record again for tracks, paint and missing sounds.
  Recording stops and saves on scene exit, after 20 minutes or at its bounded pose/geometry limit.
- Camera: right drag look, middle drag pan, wheel zoom, W A S D and R / F fly. Esc leaves for the pause menu.
  Click and drag a camera key's model on the map to move it; Shift-drag changes height. Camera selection keeps the
  editing view in place. Ctrl-drag rotates; Yaw / Pitch / Roll +/-5 degree buttons turn the selected camera key.
  Manual rotation disables automatic Look at and preserves position, time, zoom and the followed-tank frame.
  New cameras have a key immediately. Height +/-1 m, Use view and FOV +/-5 edit the key.
  Camera view shows its live POV without camera/path gizmos. View here moves the editor into it. Scrubbing and
  dragging update the camera view. F9 stops replay Preview/Play and returns to editing. After live editor Play,
  F9 returns to the same editor; it can toggle back again, including after leaving with Esc.

THE COMMAND VIEW (F10)
Select any tanks (left click or a box), right click to move or attack, Space pauses. Take control of one tank: arrow
keys drive, the mouse aims, G fires. Force stop cancels selected AI tanks' routes and brakes them;
new move/attack orders release it, and Release to AI restores normal autonomy. In Free for all, any other tank
can be targeted regardless of its placement group.

GAUNTLET FUNDS
Your first tank's full price comes out of the starting budget. Completing a round earns 10,000
plus 2,500 times that round's number, independent of starting funds. Era unlock costs also do not scale with starting
funds: era progression, tank prices, mass, dates and explicit custom costs determine them. Pay only the difference
to a later unlocked tier; earlier unlocked eras are free. Existing saved runs refresh old scaled fees while preserving
credits, repairs and unlocked eras. Before the next round, use the full native designer to repair or refit.
- Mandatory repair: 30% of the previous tank's price times its component damage fraction. Native component health
  is read without changing it. If a complete health reading is unavailable, the repair quote assumes full damage.
- Upgrade: any positive increase in tank price. Changing the design also costs 5% of the resulting tank price as
  refit labour. Saving an unchanged copy or changing its name does not charge refit labour.
- Downgrades give no refund. Quotes must fit remaining funds; charges and rewards happen once per round.
Complete the selected 2 to 30 rounds to win. Losing your tank ends the run.

FILES
Battles: Documents\My Games\Sprocket\Battles\<name>.json; shared ones: Battles\Shared. Private tank copies live in
Battles\Drafts; retain drafts used by a saved battle. Sharing includes them. Drive and turret times per
design: BepInEx\config\SprocketBattles-travel.json. Design shapes: BepInEx\cache\SprocketBattles-shapes. Log for bug
reports: BepInEx\SprocketBattles-trace.log.

SPAWN SAFETY
Custom battles pass authored positions and rotations to the native spawner before vehicle construction. Staging
points clear nearby terrain; the game builds track and powertrain state at those positions. Completed tanks keep
their native physics poses. Invalid startup physics is reported once in the trace; it is not repaired by teleporting
an enabled tank, which cannot reset the game's private physics job buffers.
While editing, tank renderers are hidden and restored without moving the battle's live physics bodies.
Play restores the editor's camera, controls and renderer references, then waits for a normal physics/frame cycle
before the game's Retry releases vehicles at the end of the frame, after native Update/LateUpdate.
The native collider-cleanup crash still needs gameplay confirmation.
Editing waits for native map setup to finish. If Play cannot prepare its spawn plan, the editor stays open and
the previous loading lineup is restored; the reason is displayed and logged instead of resuming default spawns.

This is an alpha. Build and offline tests do not verify native gameplay or on-screen layout;
these changes are provided for the user's in-game test. Report problems on GitHub with the log.
