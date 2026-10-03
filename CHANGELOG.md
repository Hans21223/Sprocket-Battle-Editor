# Battle Editor 0.17.0

- The player picks their own tanks: mark Team 1's tanks "Player picks it" in the editor and set limits (a budget for
  all of them, a cost each, a run of eras, custom eras too; how many is how many are marked). Playing such a battle
  opens the game's own Custom Battle screen to pick on, then Start battle checks the picks and puts them in place of
  the marked tanks, keeping their places, orders and parts in the mission.
- Battle file: `pick` on a tank, `limits` on the battle (older files read as before).

# Battle Editor 0.16.1 + Map Framework 0.3.1

- Package both mods in one install ZIP, including the Sandbox thumbnails beside `SprocketMaps.dll`.
- Include Map Framework source in this repository and provide a repeatable packaging script with version and file checks.
- Keep Map Framework behavior at 0.3.1; align both DLL assembly versions with their plugin versions.
- Build both plugins against Sprocket 0.2.55.5 interop and run the offline battle-format, drivetrain and sharing tests.

This remains an alpha. The bundle has not been run in a new game session; see README for existing runtime limitations.
