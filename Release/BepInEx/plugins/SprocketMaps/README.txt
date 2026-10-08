Map Framework 0.4.1 (Alpha)
https://github.com/Hans21223/Sprocket-Battle-Editor

For Sprocket 0.2.56.0 with the Sprocket Mod Loader (BepInEx 6 IL2CPP).
Rebuilt against fresh game interop; offline regression, patch-target and native scene-loader ABI checks pass.
In-game testing remains pending. Unity remains 6000.3.21f1.

Included maps: Ambush (repaired team setup), The Crossroad, Silent Border, Sandbox and Sandbox (Low performance).
The maps appear in the game's Custom Battle list and in Battle Editor's map picker. No scenario files are replaced.
Third-party Demo City and FNAF scenery used in local tests is excluded from public downloads. Import your own
licensed sources or add compatible scene bundles to CustomMaps. Existing local maps remain usable.
See Map-Asset-Provenance.txt for local fixture origins and distribution scope.

Keep SprocketMaps.dll, Sandbox.png and Sandbox (Low performance).png together in BepInEx\plugins\SprocketMaps.
When updating, remove any older extra copy of SprocketMaps.dll so only one copy is installed.

Automatic import: put OBJ/FBX models and their textures in a subfolder of CustomMaps, or put ZIP, 7z and
.unitypackage files directly in CustomMaps. Nested ZIP/7z archives are unpacked automatically. Unity 6000.3.21f1
must be installed and licensed; Map Framework converts a private copy in the background with no manual export.
Keep the included SharpCompress.dll beside SprocketMaps.dll. Other Unity locations and an additional map source
folder can be set under Import in BepInEx/config/local.sprocket.maps.cfg.
Reopen the map picker after the import notice finishes. Verified cached maps can load without Unity installed.
Converted maps are cached until their source changes. Scanning and conversion run in the background.
An explicitly selected OBJ/FBX imports that model and its texture/material sidecars, not neighboring models.
Restart to use changes to an already registered map. A package uses its largest scene, or largest model/prefab.
Conventional textures/colours and authored transforms are retained. Scripts and custom shader behaviour are not
imported. Maps need solid navigable ground at least 100 metres across both axes with two clear areas 100 metres
apart. Failed imports report their diagnostic log and do not modify native maps or the supplied source files.

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
