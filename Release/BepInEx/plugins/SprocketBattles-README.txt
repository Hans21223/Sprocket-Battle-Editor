Sprocket Battle Editor 0.16.1 + Map Framework 0.3.1 - for Sprocket 0.2.55.5
https://github.com/Hans21223/Sprocket-Battle-Editor

INSTALL
Needs the Sprocket Mod Loader (BepInEx 6 IL2CPP). Add the zip in Sprocket Mod Manager, or copy its BepInEx folder into
the Sprocket folder.
Both plugins are included. Map Framework and its two Sandbox PNGs live together in BepInEx\plugins\SprocketMaps.
Remove any older extra copy of SprocketMaps.dll when updating, so only one copy is installed. Install the loader
separately; this package contains no loader or game files.

MAP FRAMEWORK
Makes Ambush, The Crossroad, Silent Border, Sandbox and Sandbox (Low performance) available in Custom Battle and
Battle Editor. Repairs Ambush's team setup and builds custom-battle setups on the other maps. Keep the Sandbox
pictures beside SprocketMaps.dll. Map Framework's gameplay behavior is unchanged from 0.3.1.

START
- Main menu > Battle Editor (under Custom Battle): your battles on the game's Scenarios screen (the list scrolls).
  New battle picks a map and opens the editor; Quick battle starts random tanks a side (any map, any era) with
  nothing to set up. Click a battle for Play, Edit, Rename, Description, Objectives, Fails if, Clouds, Fog,
  Duplicate, Share, Delete.
- Share writes the battle as one zip in Documents\My Games\Sprocket\Battles\Shared, with the designs of yours it uses
  and their paint and decal pictures. Import a shared battle puts one in from there or from Downloads; its designs
  go into a faction of their own, "Shared battles".
- In any custom battle: F9 opens the editor, F10 the command view.

THE EDITOR (F9)
- Tanks: pick a faction and an era (the game's eras, custom ones too), then a design; click the ground to place it
  for a team (drag to point it), drag to move, Q / E to turn, Delete to remove. A tank shows as its design's shape
  once that design has been in a battle. Per tank: who drives it, a path, a main target, reserve. Play restarts the
  battle with your tanks. Load saved lists the battles saved on the map.
- Mission: zones, AT mines, hedgehogs, concrete blocks, the game's AT guns, and rules ("when this happens, do that":
  messages, victory, defeat, artillery with a shell size, reserves, drive to a zone).
- Cinematic: cameras with keys (follow and look at a tank), cuts, a timeline with an End mark, Preview, and tank keys
  (aim, shoot at, drive, drive to, fire) with estimated turret and drive times.
- Camera: right drag look, middle drag pan, wheel zoom, W A S D and R / F fly. Esc leaves for the pause menu.

THE COMMAND VIEW (F10)
Select any tanks (left click or a box), right click to move or attack, Space pauses. Take control of one tank: arrow
keys drive, the mouse aims, G fires.

FILES
Battles: Documents\My Games\Sprocket\Battles\<name>.json; shared ones: Battles\Shared. Drive and turret times per
design: BepInEx\config\SprocketBattles-travel.json. Design shapes: BepInEx\cache\SprocketBattles-shapes. Log for bug
reports: BepInEx\SprocketBattles-trace.log.

This is an alpha: some parts haven't been tried in the game yet. Report problems on GitHub with the log.
