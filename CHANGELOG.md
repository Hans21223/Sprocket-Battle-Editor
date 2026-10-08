# Battle Editor 0.17.30 + Map Framework 0.4.1 (Alpha)

<!-- sp-compat {"hamish.sprocket": "0.2.56.0", "bepinex.bepinex": "6.0.0-be.788"} -->

- Publish the accumulated battle preparation, Free-for-All, Gauntlet, force-control, spawn-planning,
  replay/cinematic camera, track/paint/audio capture, save-validation and sharing improvements below.
- Include Map Framework's custom scene loader, checked spawn/navigation and automatic OBJ, FBX and Unity-package
  import. Public downloads contain plugin code and dependencies; third-party local test scenery is excluded.

- Rebuild Battle Editor 0.17.30 and Map Framework 0.4.1 against newly generated 0.2.56.0 interop.
- Verify all 24 Battle Editor and 5 Map Framework Harmony patch targets against the new game metadata.
  The native scene loader's Windows x64 cancellation-token ABI, 23 game type contracts and native
  scene/controller call layouts remain unchanged from 0.2.55.5. No game API repair is required.
- Pass all 22 offline regression suites. Unity remains 6000.3.21f1 and the nine native scenario
  configuration files are unchanged. Gameplay validation remains pending.
- Require explicit verified build outputs or a fresh GameDir rebuild when packaging. Include SharpCompress,
  its license and install assets through an allowlist. Produce separate or combined ZIPs,
  verify every entry hash and retain existing archives on name collisions.
- Keep third-party map bundles, manifests and the local demo thumbnail out of Git and public packages.
  Record fixture provenance; local maps and automatic import remain usable.

# Map Framework 0.4.1 (local test build)

- Move automatic source discovery and file metadata scanning off the game thread. Bound queued sources and
  diagnostics, invalidate obsolete results when files/settings change, and cancel conversions when disabled.
- Verify and reuse cached maps before looking for Unity. Reject malformed cache reports without disabling
  runtime maps; serialize cache access across processes and clean temporary projects after failures/cancellation.
- Import an explicitly selected OBJ/FBX with its sidecars rather than unrelated neighboring scenes/models.
  Frame source fingerprints, recheck source bytes after conversion, and preserve transparent/emissive materials.
- Use chunked compression for new imports and losslessly recompress both included map bundles. Keep every
  serialized scene/asset byte, including baked lighting. Larger on-disk bundles avoid whole-map startup inflation.
- Reduce fallback spawn-pair allocation from about 51.7 MB to 60.6 KB without changing the chosen pairs/tie order.
  Reject invalid geometry before native navigation/physics and contain native-loader recovery exceptions.
- Keep later imports unavailable after the custom loader fails. Use friendly map labels without changing scene
  identifiers or checked capacities: Demo City (Night), 8 per side; FNAF 2 Pizzeria, 1 per side.
- Supported plugin build, offline regression suites and matching-Unity conversion/bundle-opening checks are
  separate from user gameplay testing. These changes are included in the public 0.17.30 Alpha release;
  the third-party scenery from this historical local test build is omitted from public packages.

# Map Framework 0.4.0 (local test build)

- Include the converted demo_city_night and FNAF 2 Pizzeria custom maps inside Map Framework's own ZIP and
  Mod Manager entry. Included maps do not require an installed Unity editor or a separate map mod.
- Automatically import OBJ, FBX and Unity packages from CustomMaps or an additional configured source folder.
  Unpack nested ZIP/7z archives, retain texture and package GUID references, and exclude scripts/executables.
- Convert private source copies with the installed, licensed Unity 6000.3.21f1 editor. Generate readable solid
  collision, native tank navigation and checked formations at capacities 16, 8, 4, 2 or 1 per side. Preserve
  supplied geometry/placement and adapt conventional material colours, textures and texture transforms.
- Cache successful conversions by source content and converter version; verify cached bundle hashes before use.
  Show import progress/failures, cancel the owned editor on game exit, and leave native scene definitions intact.
- Include SharpCompress 0.50.4 beside the plugin. Automatic import needs the matching Unity editor; ready-made
  map bundles retain their existing loading path. Scripts and custom shader behaviour are not supported.
- Offline nested OBJ, FBX and Unity package conversion checks and regression suites are separate from gameplay.

# Battle Editor 0.17.30 + Map Framework 0.3.6 (local test build)

- Rotate cinematic camera keys with Ctrl-drag, or Yaw / Pitch / Roll +/-5 degree buttons. The POV updates live.
  Manual rotation disables automatic Look at so aiming cannot override the chosen orientation. Preserve position,
  key time, field of view and the followed tank's coordinate frame; reject invalid quaternion/heading input.
- Camera rotation/follow-frame regression checks and the existing 20 offline suites pass. Native dragging and
  preview interaction await user testing. Map Framework remains 0.3.6.

# Battle Editor 0.17.29 + Map Framework 0.3.6 (local test build)

- Fix the missing Cinematic timeline: the camera POV used a stripped DrawTexture method that threw before the
  timeline rendered. Use the available native image-label renderer with a matching preview target size, draw
  the timeline first, and guard optional POV/detail panels independently.
- Separate the recorded replay workspace from battle setup. Show a Replay Editor heading, recorded movement
  details, playback speed, Save replay edits, Play replay and Back to battle/Main menu controls. Battle recording,
  team/mission tabs, battle speed and Play-on-start controls stay in normal Cinematic setup.
- Open saved replay edits in the same workspace as original recordings. Reset stale camera/timeline tools and
  selections when changing files; filter saved-file lists to the current workspace and restore the prior battle
  tab when returning. Original recordings remain unchanged.
- Map Framework remains 0.3.6. Native display and controls await user testing.

# Battle Editor 0.17.28 + Map Framework 0.3.6 (local test build)

- Capture the game's instanced track segment meshes and world matrices, convert them into hull space, and store
  bounded compressed segment pose tracks. Preview interpolates the recorded belts without native track physics.
  Hide and restore the native instanced belts alongside tank models while editing.
- Preserve the final renderer's native paint shader, keywords, material properties and per-renderer overrides,
  including paint tint, paint textures, weathering and paint coordinate matrices. Prioritize visible paint textures.
- Record actual native one-shot/play events and running audio sources. Retain clip identity, spatial location,
  volume, pitch, loop state, sample timing and stop keys. Embed readable PCM with bounded decompression; resolve
  compressed/streamed clips against exact loaded game assets and report unresolved clips.
- Preview/Play creates its own sound sources/listener, seeks from the timeline cursor, and stops/releases sound on
  F9, Esc, preview stop and editor exit. Restore the original listeners and native source mute settings.
- New recordings are required for track, paint and sound data absent from older recordings. Existing saves and
  recordings remain readable. Build and 20 offline suites pass; native capture/playback awaits user testing.
  Map Framework remains 0.3.6.

# Battle Editor 0.17.27 + Map Framework 0.3.6 (local test build)

- F9 stops replay Preview/Play without closing the editor. Live battles started with editor Play retain a return
  permission for that battle's scene, and F9 toggling keeps the editor camera and original gameplay speed.
- Click and drag camera key models without jumping the editor into their POV. Shift-drag adjusts height;
  all camera tracks' key models can be picked. Moving a follow camera preserves its tank-relative coordinates,
  time, orientation and zoom. New cameras begin with a key; height, zoom and Use view controls edit selected keys.
- Add a live camera POV panel using a separate camera/render target. Scrubbing and dragging update it; camera/path
  gizmos are excluded. View here explicitly enters that camera's POV. Release preview resources on editor exit.
- F10 > Record current battle captures an already-running Battle Editor battle without retrying or respawning it.
  Recording captures actual live tank geometry and movement; editable replay remains 3D data rather than a video.
- Release build and 19 offline test suites pass, with both existing user recordings readable. Native camera POV,
  mouse dragging and F9 transitions await user testing. Map Framework remains 0.3.6.

# Battle Editor 0.17.26 + Map Framework 0.3.6 (local test build)

- Fix flat tank silhouettes during replay: use lit materials and capture native paint textures, normals, UVs
  and submesh material assignments. Preserve recorded local part poses; replay remains visual-only.
- Capture native renderer groups, one LOD per model, excluding shadow proxies and duplicate collider meshes.
  Read GPU-only meshes and posed skinned meshes instead of silently dropping them.
- Hide frozen native track models outside the tank hierarchy and keep LOD updates from showing them during
  replay/editor preview. Restore their original visibility when leaving the editor without moving live physics.
- Deduplicate geometry by content across tanks and reserve a geometry allowance for each distinct design.
  Raise total shared geometry to one million vertices, bound texture/save storage, and try smaller LODs when needed.
- Keep existing recordings readable with improved lit shading. Models and textures absent from an older recording
  require a new recording. Original recordings and custom map files are preserved.
- Build and offline tests pass, including the user's existing replay and saved battles. In-game capture/playback
  awaits user testing. Map Framework stays at 0.3.6.

# Battle Editor 0.17.25 + Map Framework 0.3.6 (local test build)

- Add Record replay and a replay list with Edit in Cinematic. Capture tank movement, local visual part poses and
  a camera path; scrub and preview render-only actors while editing camera keys, follow targets and cuts.
- Save original recordings separately from edited cinematic battles, validate replay data before opening, and
  bound capture duration, geometry and pose storage. Replay does not record audio, particles or projectiles;
  material colours are flat and unreadable/oversized geometry is simplified.
- Restore F9 after leaving the editor with Esc, including asynchronous native unpause. Preserve the original
  gameplay speed and editor camera, and invalidate the resume permission when its native battle scene changes.
- Remove starting-fund scaling from Gauntlet era fees and round rewards. Completed rounds pay 10,000 plus
  2,500 times the round number. Refresh old saved era prices without changing credits, repairs or unlocked eras.
- Map Framework 0.3.6 and the verified custom city map assets are unchanged. Build and offline regression tests
  cover replay validation/interpolation/save isolation, editor resume ownership and Gauntlet pricing/migration.
  In-game replay capture, map switching and pause-menu return await user testing.

# Battle Editor 0.17.24 + Map Framework 0.3.6 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Free-for-all AI can attack contenders in its own blue/red spawn group. Ordinary team battles retain allies.
- Gauntlet uses the map's real enemy capacity, preserves paid rounds when resuming, and advances saved victories
  to the next round with the repair bill intact. Launchers reject unknown spawn capacities instead of guessing.
- Validate battle saves, cinematic poses and track belts before passing them into native game code. Track checks
  now handle reordered fields and missing pitch. Imported cinematic keys are sorted for playback.
- Treat Windows path aliases as one native unit definition; preserve distinct same-name designs in shared ZIPs.
  Reject incomplete shared packages before creating imported files.
- Save battles, Gauntlet progress and imported designs with complete temporary files before replacement.
- Keep ownership of a closing menu's pending load so it is unloaded before another battle starts. Clear battle,
  movement and gun-control state when its owning scene exits. F9 only leaves an already-open editor.
- Bound and validate cached shape data, release replaced meshes, filter broken travel profiles, and serialize
  background trace writes. The live Gauntlet refit panel explicitly labels its labour charge as an estimate.

Release build and all 17 offline regression suites pass. Read-only checks pass for 13 user battles and 25 shipped
tank blueprints. Map Framework 0.3.6 and the validated city bundle are unchanged. Gameplay confirmation is left to
the user; this is a local build, not a published GitHub release.

# Battle Editor 0.17.23 + Map Framework 0.3.6 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Editor Save and Play discard inherited random spawn settings and preserve the tank positions placed by the user.
- Missing spawn settings no longer create a random layout by default. Random placement requires explicit launcher
  settings, so ordinary editor battles and older saved battles bypass the random spacing planner.

Release build and offline regression checks pass. Map Framework and the city export are unchanged.

# Battle Editor 0.17.22 + Map Framework 0.3.6 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix custom map spawn validation calling the stripped Unity `NavMesh.Triangulate` wrapper after collision
  and navigation had successfully built. Use the game's compiled path queries instead. Check every spawn
  footprint against complete local paths, verify actual path endpoints so nearby roads and stacked roofs
  cannot supply false coverage, reuse the native path, cache ground checks and bound expensive queries.
- Cancel editor entry as soon as a custom load fails, before the recovery carrier can expose its running
  tanks. Wait for completed custom initialization before opening an editor, release the view when its
  owning scene unloads, and abort vehicle-designer preparation without reopening the failed map.
- Add regression checks for local coverage, disconnected routes, projected endpoints, invalid positions,
  successful readiness, abandoned loads and recovery carriers that must never become editable maps.

Both Release builds and offline checks pass. The unchanged city export remains independently validated;
in-game confirmation is left to the user.

# Battle Editor 0.17.21 + Map Framework 0.3.5 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix the native startup crash introduced by the custom scene-load hook. Windows x64 passes its eight-byte
  cancellation token by value; the generated Harmony trampoline boxed that value as a memory address and
  dereferenced zero for an empty token. Use an explicit native ABI hook, preserve all native request arguments
  and task return values, and supply real storage only when a custom request needs a boxed cancellation token.
- Validate the token layout before attaching and add an unmanaged callback regression check for empty and
  nonempty tokens. The rebuilt city collision/navigation export remains unchanged.

Build and offline ABI checks are separate from the user's in-game confirmation.

# Battle Editor 0.17.20 + Map Framework 0.3.4 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Preserve built-in scene definitions, native player cameras and shared materials. Remove the previous native
  scene preloading and controller-template replacement that could leave Sandbox black.
- Load custom environment bundles through the game's authentic native controller. Wait for scenery, ground
  collision and tank navigation before starting the vehicle designer or spawning battle tanks.
- Convert custom materials with separate HDRP copies, preserving textures and leaving native map assets intact.
- Use connected, individually checked spawn formations, adapt the designer's initial placement and world bounds,
  and replace only the carrier map's owned navigation data for the custom session.
- Restore scene changes on exit, clean up canceled additive loads and settle the native loading task once. A map
  that cannot start returns to the main menu with an explanation instead of leaving a covered loading screen.
- Keep custom map identities through designer preparation and native Retry. Reject unavailable saved maps before
  leaving the menu. Battle Editor continues to work without Map Framework.
- Add offline lifecycle, navigation and native map ownership regression checks.
- Honor authored custom map capacities and use compact formations. Check each offered tank slot instead of
  requiring every city to accommodate 16 tanks per side; native maps retain their existing placement.

This is a local build; no GitHub push or release is implied. Build and offline validation remain separate from
the user's in-game confirmation.

# Battle Editor 0.17.19 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Quick battle has separate Player faction and Enemy faction selectors. Each side draws random designs only from
  its own selection. The same choices apply to the starting groups in Free-for-All.
- Gauntlet has an Enemy faction selector; all rounds use that selected faction while the player chooses and edits
  their tank in the full vehicle designer.
- Expand a selector to choose a named faction, all your factions, base-game tanks or all factions. Faction choices
  are kept by name, and existing era and broken-blueprint filters still apply. Empty choices show which side has
  no usable tanks rather than silently choosing another faction.
- Add offline checks for independent sides, exact faction matching, source restrictions, empty selections and defaults.
- Keep Quick battle, Gauntlet and their faction selectors available in the fallback menu if the native Scenarios
  screen is unavailable. Both menus share setting and start actions, with scrolling and numeric editing.

This is a local build; no GitHub push or release is implied. Gameplay confirmation remains with the user.

# Battle Editor 0.17.18 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Remove F9 as an editor entry during play. Open Edit battle from the menu; F9 can still leave an already-open
  editor, and F10 remains the command view.
- Add random Free-for-All spawn points with an editable nearest-opponent spacing range, from 16 to 2,000 metres.
  Keep placement before native vehicle construction, without relocating live track physics. Quick battle and
  saved battle settings expose the controls.
- Give Free-for-All AI an opponent to attack or approach automatically. Explicit Force stop, player driving,
  authored paths and manual move/attack orders take priority over automatic actions.
- Add Gauntlet under Playing: a starting budget, map, opponent pool, era and 2 to 30 rounds. Buy one tank in the
  full native vehicle designer; opponents grow in count and cost within the map's native capacity, up to eight.
  Return to the full designer between rounds for repairs and refits within the remaining funds.
- Charge mandatory repairs at 30% of the previous tank price times its native component damage fraction;
  an unavailable complete health reading assumes full damage. Charge positive price increases plus 5% of the
  resulting tank price when a design changes. Downgrades do not produce refunds, and unchanged saved copies
  or renamed tanks do not incur refit labour.
- Each completed round rewards 10% of starting funds plus 2,500 times the round number. Charges and rewards
  are paid once per round. Complete the configured rounds to win; losing the player tank ends the run.
- Route Free-for-All Retry through spawn planning and contender registration. Gauntlet uses paid intermissions
  instead of raw Retry, which would replace tanks without updating the run or charging repairs.
- Release special-mode state on scene exit, use the enemy side's map capacity for Gauntlet, suppress hidden
  reserve drivers and gun crews, and let pending native manual paths start before declaring them finished.
- Add offline checks for spawn spacing, mode serialization, Gauntlet finances, fingerprints and wave progression.

This is a local build; nothing in this entry indicates a GitHub push or published release. Offline checks remain
separate from the user's gameplay confirmation.

# Battle Editor 0.17.17 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Add Free-for-All to the editor, saved battle settings and Quick battle. Each tank is its own opponent;
  the last mobile tank wins. Native independent faction masks limit this mode to eight tanks including reserves.
  Blue/red remain placement groups and mission-rule groups; each loading group needs a starting tank.
- Refresh native detection records and AI friend/enemy filters together. Block the native two-team result while
  Free-for-All runs, and restore original team identities when leaving or restarting the battle.
- Add Force stop in F10 and Hold position per authored tank. Cancel driver routes and formation movement,
  brake using the ordinary vehicle controls and keep aiming/firing active. New movement or attack orders,
  Release to AI and player driving release the stop.
- Authored tanks with no movement orders hold their ground, including after their paths finish. Quick battles
  retain autonomous AI unless explicitly stopped. Signed braking uses a rest band to avoid alternating directions.
- Add offline checks for saved mode/hold settings, FFA capacity and independent faction masks, winner/draw/reserve
  handling, and movement transitions and braking.

Build and offline regression checks remain separate from the user's gameplay confirmation.

# Battle Editor 0.17.16 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Remove can clear the last selected tank. The designer keeps that tank as an unselected preview; selected count
  and budget become zero immediately. Add tank includes the preview again, and Play requires a selected tank.
- Show selected count and maximum separately. Keep lineup controls available for a one-tank battle and clearly
  dim disabled buttons, including the previous/next arrows when fewer than two tanks are selected.
- Refresh count and budget on lineup actions. Keep explicit save/add errors visible above generic preview guidance.

Build and offline regression checks remain separate from the user's gameplay confirmation.

# Battle Editor 0.17.15 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Validate the editor's hidden loading tank in both launch paths. Prefer a shipped tank with valid tracks, skip
  broken or unreadable designs, and refuse to load when no usable candidate exists. Previously the first faction
  design was used without checking it, even after the design list identified its missing track segments.
- Log the selected loading blueprint and skipped candidates. Validate all teams before changing the lineup.
- The two 0.17.14 dumps fail while native teardown frees track suspension or bogie buffers, before replacement
  tanks are constructed. The first faction design in those runs has two empty track segment IDs. This unchecked
  loading path is fixed; gameplay confirmation is still needed to establish whether it caused the corruption.

Build and offline regression checks remain separate from the user's gameplay confirmation.

# Battle Editor 0.17.14 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Queue Play's native Retry until the editor has restored tank visibility, input, HUD, fog and camera references.
  Previously Retry released vehicles synchronously before those references were restored. Wait for a resumed
  physics step and a complete frame, then start Retry after native Update/LateUpdate at the end of the frame;
  pending or repeated requests cannot run it twice.
- Keep registration waiting until Retry starts and recheck that native setup and authored spawn points are ready.
  Trace both the queued request and the eventual native restart so failures can be located precisely.
- The supplied crash dump fails in native heap cleanup while destroying a tank MeshCollider. The unsafe editor/
  Retry ordering above is confirmed; this change has not yet been shown to eliminate that native crash in gameplay.

Build and offline regression checks remain separate from the user's gameplay confirmation.

# Battle Editor 0.17.13 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix Retry's native team-map lookup: it uses generated game-team IDs, not editable lineup indices. The 0.17.12
  lookup asked for ID 0 and rejected authored positions before retrying, leaving the game's loading vehicles visible.
- Keep the editor open on failed Play, log the full error and restore the previous lineup. Successful Play still
  supplies authored positions before physics construction, with no live tank teleport.
- Start editor loading with one placeholder per team instead of retaining a previous native lineup. Wait until
  native setup reaches Running before freezing the map or accepting Retry; one registered tank is not a complete load.

Build and offline checks remain separate from the user's gameplay confirmation.

# Battle Editor 0.17.12 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Supply authored tank positions and rotations through native team spawn locators before vehicle construction,
  on both initial loading and Retry. Reset allocator order, write native team value types back into their array,
  verify locator assignment and restore the original locators when leaving the battle.
- Remove post-spawn relocation and transform-based recovery. The 0.17.11 test log shows tanks jumping hundreds of
  metres just after relocation; native physics sync reapplies cached powertrain Rigidbody state. Track setup now
  only logs its native pose, without moving an assembly during the component enable loop.
- Keep bounded startup diagnostics for invalid physics, without attempting another live teleport.
- Hide tank renderers while editing instead of moving live tanks under the map. Leaving the editor restores each
  renderer's previous visibility without changing physics poses or velocities.
- Check attacker/defender routing, including swapped player roles and retries, before assigning native locators.
  Add offline regression checks for those mappings and invalid setups.

Build and offline checks do not establish that the in-game launch is fixed. This build is provided for the user's
test of the same battle that failed in 0.17.11.

# Battle Editor 0.17.11 + Map Framework 0.3.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Check custom-battle spawn clearance before track physics is assembled, while the main body is still kinematic.
  Use the built vehicle's footprint and map terrain; keep one metre of staging clearance for components still being
  assembled. Attach through the reference-only track setup method, avoiding the unsafe spawn method signature.
- Place each completed tank immediately, with terrain clearance across its footprint after its requested rotation,
  instead of waiting for all tanks and checking only the ground under their origins.
- Move connected bodies, intermediate transforms and world joint anchors together from poses captured before any
  transform changes. Startup NaN recovery uses retained healthy poses for the whole assembly and runs independently
  of AI timing; it cannot rebuild destroyed tanks. Preserve valid mass settings and zero inertia axes.
- Map Framework explicitly disables connecting-line randomization and retains conservative staging clearance. Native
  disassembly confirms that SpawnPoints.Get preserves child heights; ground snapping is a separate authoring method.
- Add offline tests for sloped terrain, footprint boundaries, missing ground, nonfinite inputs and bounded sampling.

Both plugins are built and packaged together. Gameplay effects still require the user's in-game test; the previous
log's high/NaN vehicle positions do not establish a single root cause by themselves.

# Battle Editor 0.17.10 + Map Framework 0.3.2 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix tanks dropping from the sky and blowing up at the start of a battle on the framework's maps (Sandbox, The
  Crossroad, Silent Border). Some of the setups' spawn points were snapped onto something about 250 m up, and the
  Battle Editor kept a tank's spawn height when moving it to its place. Tanks are now set on the ground by their own
  shape; one left at its spawn point that spawned in the air is set down. The trace says when a tank spawned high.
- Map Framework: spawn points snap to the map's terrain (the log names any that didn't land on terrain).

# Battle Editor 0.17.9 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Tanks are moved to their places whole: bodies joined to a tank from outside its own object move with it, and its
  joints fixed to the world move their anchor too. A battle played from the editor sometimes threw the tank you drive
  into the sky (seen since 2026-10-03, not caused by 0.17.6); a thrown tank is also put back once in the first 8 s.
  The trace says what was found.

# Battle Editor 0.17.8 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix tanks thrown into the sky (and the view going grey) at the start of a battle played from the editor (0.17.6,
  0.17.7). A tank holding its ground was given no order at all; it now gets an order to stay where it was placed,
  facing as placed, again after any attack order and every 10 s. The trace names any tank still thrown.

# Battle Editor 0.17.7 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix the grey screen at the start of a battle (0.17.6): holding tanks with no path also held the tank you drive and
  took every tank out of its formation. The tank you drive is left alone, and holding tanks keep their formation.
- Share locked: the shared copy can be played but not edited. It stays out of the Editor tab and Load saved, and Edit
  refuses it. Your own battle stays editable.

# Battle Editor 0.17.6 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix the black screen after Play in the editor with no Team 1 tank on the map (the game had no tank to put you in).
  Play now says so instead.
- Block broken designs: tracks with no track segment (older saves) or no segment pitch. Quick battle never picks them,
  battles refuse them, and the designer won't open on or play one. One such design crashed the game on leaving its battle.
- FieldsLightAT is a tank, not an AT gun: it's no longer offered by the AT gun tool.
- A tank with no path holds its ground and only shoots (the game's own orders are dropped); with a target and no path
  it shoots that target from where it stands. Quick battles keep the game's AI.
- Per tank: Turret turns and Shoots, to keep its AI from turning the turret or firing.

# Battle Editor 0.17.5 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Remove the designer overlay's Back to battles button: the game's Esc → Main menu already returns to the battle list.
- Don't record shapes and drive times for designer drafts (a new one-off file each Play). Investigating a crash on
  leaving such a battle: the game freed a tank track's memory twice in its own clean-up.

# Battle Editor 0.17.4 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Fix the crash as the designer opened on a battle's map (0.17.3). Its hook on the scenario editor's update crashed
  the game in the .NET runtime on its first call. The designer's Play arrow is now caught by wrapping the designer's
  own state-change event instead, with no hook on game methods.
- Show why the designer couldn't open, back on the menu.

# Battle Editor 0.17.3 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Move player tank limits into **Edit battle → Mission → Player tank limits**, alongside mission authoring.
  Remove their editing controls from the saved-battle menu. Keep count, total budget, per-tank cost, player
  positions and individual allowed-era controls together.
- Prepare player tanks in the full native vehicle designer on the selected battle map.
- Intercept the native designer's mission transition before it can start the map's default mission. Validate
  and launch the configured custom battle instead.
- Serialize private battle drafts through the native serializer without rejecting its uninitialized in-memory
  validity flag.

Build and offline checks are separate from the user's in-game test. Map Framework remains 0.3.1.

# Battle Editor 0.17.2 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Open **Choose tanks & play** directly in the game's full vehicle designer, with its normal tank viewport,
  part categories, Save and Load buttons. Show the battle's limits in the native Scenario display.
- Start the custom battle with the designer's Play arrow or Space. Recheck tank count, total budget, cost per
  tank, allowed eras and the game's tank-validity checks before starting. Keep fixed-tank play and the battle
  author's editor available.
- Keep unsaved tank edits in private battle drafts instead of overwriting personal blueprint files. Add, switch
  and remove lineup tanks without losing the other tanks' edits. Retain drafts for retrying, saving and sharing.
- Keep the player's driving position when choosing fewer tanks than the battle provides.
- Keep action labels readable when the game's original button is narrow.

Build and offline regression tests are checked separately from gameplay. The direct designer flow, native Scenario
display and Play handoff still need the user's in-game test. Map Framework remains 0.3.1.

# Battle Editor 0.17.1 (local test build)

<!-- sp-compat {"hamish.sprocket": "0.2.55.5", "bepinex.bepinex": "6.0.0-be.788"} -->

- Add visible **Editor** and **Playing** tabs above the saved-battle list. Editor contains battle creation, editing,
  sharing and limits; Playing contains Quick battle, player tank selection and playing the authored lineup.
- Add maximum player tank count, total tank budget, cost per tank and individual allowed-era controls to the menu.
  Add player positions without copying an existing tank's orders or mission references. Existing battle files remain compatible.
- Add **Edit selected tank** in player setup: open the selected design in the game's full Sandbox vehicle designer,
  then **Save & return to battle** or return without saving. Preserve the battle, lineup and counts, including a new
  save path, and reload saved design prices before starting.
- Reject picks above the count limit, invalid costs and totals that exceed the budget without integer overflow.
  Empty roster rows no longer become extra tanks; leaving setup restores the native budget and capacity.
- Forward wheel and drag events from battle rows to the scrolling list, including the screen variant without a
  built-in scroll view. Keep text selection usable while editing a field.
- Fix IL2CPP corner-array handling so tabs and picker buttons use their actual screen positions and sizes.

Build and offline regression tests are checked separately from gameplay. The native designer round trip and menu
layout still need the user's in-game test. Map Framework remains 0.3.1.

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
