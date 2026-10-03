Sprocket Battle Editor - make your own battles on Sprocket's custom battle maps, for Sprocket 0.2.55.5
https://github.com/Hans21223/Sprocket-Battle-Editor

INSTALL
Needs the Sprocket Mod Loader (BepInEx 6 IL2CPP). Add the zip in Sprocket Mod Manager, or copy its BepInEx folder into
the Sprocket folder.

START
- Main menu > Battle Editor (under Custom Battle): your battles on the game's Scenarios screen. New battle picks
  a map and opens the editor; Quick battle starts random tanks a side on a map with nothing to set up. Click a battle for Play, Edit, Rename, Description, Clouds, Fog, Duplicate, Delete.
- In any custom battle: F9 opens the editor, F10 the command view.

THE EDITOR (F9)
- Tanks: click the ground to place a design for a team (drag to point it), drag to move, Q / E to turn, Delete to
  remove. Per tank: who drives it, a path, a main target, reserve. Play restarts the battle with your tanks.
- Mission: zones, AT mines, hedgehogs, concrete blocks, the game's AT guns, and rules ("when this happens, do that":
  messages, victory, defeat, artillery with a shell size, reserves, drive to a zone).
- Cinematic: cameras with keys (follow and look at a tank), cuts, a timeline with an End mark, Preview, and tank keys
  (aim, shoot at, drive, drive to, fire) with estimated turret and drive times.
- Camera: right drag look, middle drag pan, wheel zoom, W A S D and R / F fly. Esc leaves for the pause menu.

THE COMMAND VIEW (F10)
Select any tanks (left click or a box), right click to move or attack, Space pauses. Take control of one tank: arrow
keys drive, the mouse aims, G fires.

FILES
Battles: Documents\My Games\Sprocket\Battles\<name>.json. Drive and turret times per design:
BepInEx\config\SprocketBattles-travel.json. Log for bug reports: BepInEx\SprocketBattles-trace.log.

This is a pre-release: some parts haven't been tried in the game yet. Report problems on GitHub with the log.
