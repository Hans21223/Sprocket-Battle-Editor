Map Framework 1.1.0 (Beta)
https://github.com/Hans21223/Sprocket-Battle-Editor

For Sprocket 0.2.55.5 and 0.2.56 with the Sprocket Mod Loader (BepInEx 6 IL2CPP). Unity remains 6000.3.21f1.
New in 1.1.0: OBJ and Garry's Mod maps don't need Unity. Without it (or when it fails), the game builds them itself
and checks them once in the main menu (Import > Build maps in the game: Automatic, Always or Never). Garry's Mod
maps bring their own sun, skybox or painted sky, fog and lamps (Lighting > GMod map light and sky).
New in 1.0.1: Unity 6000.3.21f1 is found wherever Unity Hub or Unity's installer put it. Before, only Unity Hub's
default folder and <drive>:\Unity were searched, so an install anywhere else read as "not installed".
New since 0.4.1 (details in CHANGELOG.md and docs/Making-Custom-Maps.html in the repository):
- Garry's Mod maps: put a workshop addon (.gma) or a .bsp in CustomMaps. It is converted with its props and
  textures (stock textures come from your Garry's Mod install, found through Steam) and listed under its own name.
  An old workshop item (<number>_legacy.bin) or a whole workshop item folder works too. Props collide as in Garry's
  Mod: with their collision model, their box, or not at all.
- Maps keep their own lighting: baked lightmaps, glowing materials, sky, fog and lamps (lamp distance and brightness
  under Lighting in local.sprocket.maps.cfg).
- Water surfaces, breakable scenery and trees, and Map Framework's own map shaders (sprocketmaps.shaders).
- Import fixes for older Unity assets, miniature models and town maps with narrow streets.

Included maps: Ambush (repaired team setup), The Crossroad, Silent Border, Sandbox and Sandbox (Low performance).
The maps appear in the game's Custom Battle list and in Battle Editor's map picker. No scenario files are replaced.
Third-party Demo City and FNAF scenery used in local tests is excluded from public downloads. Import your own
licensed sources or add compatible scene bundles to CustomMaps. Existing local maps remain usable.
See Map-Asset-Provenance.txt for local fixture origins and distribution scope.

Keep SprocketMaps.dll, sprocketmaps.shaders, Sandbox.png and Sandbox (Low performance).png together in
BepInEx\plugins\SprocketMaps.
When updating, remove any older extra copy of SprocketMaps.dll so only one copy is installed.

Automatic import: put OBJ/FBX models and their textures in a subfolder of CustomMaps, or put ZIP, 7z and
.unitypackage files directly in CustomMaps. Nested ZIP/7z archives are unpacked automatically. FBX models and Unity
packages need Unity 6000.3.21f1 installed and licensed; Map Framework converts a private copy in the background with
no manual export. OBJ and Garry's Mod maps use Unity when it works and are built by the game otherwise.
It finds Unity wherever Unity Hub or Unity's installer put it. Keep the included SharpCompress.dll beside
SprocketMaps.dll. A Unity copy without an installer and an additional map source folder can be set under Import in
BepInEx/config/local.sprocket.maps.cfg.
Reopen the map picker after the import notice finishes. Verified cached maps can load without Unity installed.
Converted maps are cached until their source changes. Scanning and conversion run in the background.
An explicitly selected OBJ/FBX imports that model and its texture/material sidecars, not neighboring models.
Restart to use changes to an already registered map. A package uses its largest scene, or largest model/prefab.
Conventional textures/colours and authored transforms are retained. Scripts and custom shader behaviour are not
imported. Maps need solid navigable ground at least 100 metres across both axes with two clear, connected areas
100 metres apart (half the map's width on a smaller map). Failed imports report their diagnostic log and do not modify native maps or the supplied source files.

Custom scenery bundles go in BepInEx\plugins\SprocketMaps\CustomMaps. They must be Windows scene bundles built
with Unity 6000.3.21f1, with solid ground and readable collision meshes for tank navigation. The game supplies its
normal player controls and vehicle designer. Only custom materials are adapted; native cameras and scenes stay intact.
Invalid custom maps return to the menu with an explanation. Map sources and scene bundles can be added to
the CustomMaps folder; public plugin packages contain no third-party custom scenery.
Place the map's capacity JSON beside its bundle when supplied. It declares 1 to 16 tanks per side, and the map picker
uses that capacity.

Source is included in the SprocketMaps project in the Battle Editor repository. Spawn staging samples the terrain
within eight metres and stays 2.5 metres above the highest sample. Child selection is deterministic, with connecting-
line randomization disabled. Battle Editor additionally checks actual built vehicle footprints before track physics.
Build validation and offline checks are separate from the user's in-game test.
