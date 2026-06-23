# CompetitiveAdjustments

Server-side and client-side gameplay, physics, visual, and network adjustments for **Puck (B897)**.

Built as a BepInEx-style plugin DLL loaded from `Puck/Plugins/CompetitiveAdjustments/`. Server config is a single nested JSON; client preferences are stored per-user. Clients joining a server without this mod stay fully inert. No Harmony patches install and no visuals change.

## Features

### Movement
- Skater dive, twist while sliding, slide influence.
- Goalie dive, standing dash, dash extend (speed curve), twist while sliding, stances.
- Goalie sliding reach reduction with configurable scale.

### Stamina
- Separate skater and goalie regeneration and drain curves.
- `SprintStaminaDrainRateOffset` corrects floating point drift on sprint drain.

### Visuals
- Arena rescale, offset, and rotation, with a custom arena prefab and collider clone.
- Goal net resize (per axis), thickness scale, back offset.
- Audio reverb zone follows the resized arena.
- Custom skater torso mesh and collider, with a client-side visibility toggle.
- Sprint shoulder trail (white motion lines while sprinting).
- Optional minimap rescale that tracks the synced arena scale.
- Debug clip-brush overlays for arena and player colliders.

### Stick
- Free blade.
- Higher stick (activate angle plus max angle).
- Stick-body collision.
- Mid-stick collider, disable shaft/stick collision, alter stick positioner output, stick speed decay.
- Client-side blade spin clamp (Free Blade Spin Lock) with configurable min/max.

### Puck
- Puck scale, server driven, applied to all live pucks.
- Drag tuning: speed-dependent drag, height-dependent drag.
- Ball mode, banana mode.
- Random puck drop, puck through bodies, puck through groin.

### Puck and stick physics calibration (Puck B897)

These notes record how the spawn-time overrides line up against vanilla B897, verified against the decompiled `Puck`, `Stick`, and `StickPositioner` sources. They matter because several overrides currently match vanilla exactly and so do nothing, while one deviates sharply.

Overrides that match vanilla and are effectively no-ops. `PuckMaxSpeed` (30), `PuckStickTensor` (0.006, 0.002, 0.006), and `ShaftHandleProportionalGain` (500) are identical to the game's serialized defaults, so re-applying them at spawn changes nothing.

`StickOnPuckInverseMass` defaults to 1.0, which is the neutral contact value. At 1.0 the puck-on-blade contact resolves with the real masses and the mod adds no grip. Lowering it below 1.0 makes the stick behave heavier in the contact, which is the lever for the "puck keeps disengaging" complaint.

The puck inertia tensor is re-applied by the game every `FixedUpdate`, using the larger `stickTensor` while the puck touches a stick and the smaller `defaultTensor` (0.002 on every axis) otherwise. Tuning grip through `PuckStickTensor` is therefore live, but only takes effect while the override differs from the vanilla value.

`AlterStickPositionerOutput` forces `StickPositioner.outputMin` and `outputMax` to plus or minus `StickPositionerOutputMax`. This is the PID clamp on `raycastOriginAngle`, the per second rate at which the stick aim sweeps, in other words the maximum stick swing speed. The decompiled field initializer is plus or minus 15, but the live prefab may serialize a higher value, so treat 15 as a floor reference rather than the exact vanilla number. Setting it well above the vanilla clamp removes the swing rate damping and makes the stick snappier, which can read as a looser blade and can drag the blade target through tie-ups. Tune against the actual in-game vanilla swing feel.

Tie-ups are a vanilla B897 mechanic. `Stick.Server_OnCollisionStay` reduces the blade PID gain when your blade (tag "Stick Blade") contacts another stick's shaft (tag "Stick Shaft"). `DisableShaftCollision` and the stick-on-stick contact-mass branch in `Utils.cs` both interact with this, so they are the first places to look when tie-ups feel wrong.

There is no friction coefficient anywhere in the B897 C#. Blade and puck friction lives on the prefab colliders in the asset bundle, so it is not a value the game or this mod sets in code.

### Physics and tuning (CompTweaks)
- Turn acceleration, brake, and max speed for skaters and goalies.
- Forwards, backwards, and sprint acceleration curves with scaling factors.
- Post-slide turn curve.
- Solver iterations, fixed delta time.
- Soft boards, board bounce tweak.

### Network
- **Chunked position sync** ([src/Net/](src/Net/)). Replaces the vanilla 16-bit position quantisation with a per-object chunk offset table. Vanilla precision (1.5 mm grid) is preserved while range extends to +/-4 km. Hysteresis-driven chunk handoffs with deferred apply by tickId. A client-side reject filter guards against the rare cross-channel race.
- Reliable bulk snapshot on late join, plus a client-initiated request once the client's CMM handler is registered.
- Inert on vanilla servers, gated on `_hasSyncedTweaks`.

## Repository layout

```
src/                            all source
  ArenaTweaks.cs                custom arena prefab spawn / collider sync / audio reverb / network-bounds lifecycle
  GoalNetTweaks.cs              goal net rescaling, synced-tweaks state machine, refresh runner
  StaminaPatch.cs               skater / goalie stamina drain and regen
  StickAnglePatch.cs            free blade, spin lock, high sticking
  StickOnBodyCollisions.cs      stick-body collision rules
  StickPositionerPatch.cs       stick positioner output alteration
  MovementPatch.cs              turn / accel / max-speed
  DashMod.cs / DiveMod.cs       dash and dive
  TwistMod.cs / SlideInfluenceMod.cs   twist while sliding, slide influence
  GoalieDashExtend.cs / Stances.cs     goalie dash extend, stances
  BallModePatch.cs              puck physics flavour switch
  BoardColliderPatch.cs         soft boards / bounce
  Tweaks.PlayerBodyPatch.cs     custom torso mesh, clip brushes
  Tweaks.PuckPatch.cs           puck-side patches
  Tweaks.StickPatch.cs          stick-side patches
  SprintShoulderTrail.cs        sprint shoulder trail visual
  ModMenuHub.cs                 in-game settings menu integration
  ServerConfig.cs               nested JSON config (Dashfall / CompAdjust / CompTweaks)
  DashFall.{Config,UI,HUD,Input,ClientRunner,RoleSuppression,ServerBridge,Parsing}.cs   DashFall-side runtime
  Companion.PluginCore.cs       companion (client-only) plugin
  Tweaks.PluginCore.cs          comp-tweaks plugin core
  CompetitiveAdjustmentsGameMod.cs / DashFallGameMod.cs   BasePlugin entry points
  CompatAliases.cs              global using PlayerBodyV2 = PlayerBody
  Utils.cs / SmallPatches.cs    shared helpers and version constants
  Net/
    NetworkBoundsPatch.cs       Harmony prefix on Encode/DecodeSynchronizedObject, enable/disable orchestration
    ChunkRegistry.cs            per-id ChunkSlot table and axis encode/decode helpers
    ChunkSyncServer.cs          per-tick hysteresis sweep, OWPMOD/Chunks reliable broadcasts, late-join bulk
    ChunkSyncClient.cs          CMM handler dispatch, reject filter, bulk request on enable
CompAssets/                     Unity project sources for the bundled prefabs (built externally)
libs/                           third-party DLLs referenced by csproj
```

The chunked sync system is documented in the design notes at `findings/README.md` (local reference, not part of this repo).

## Building

```pwsh
dotnet build CompetitiveAdjustments.csproj --nologo -v q
```

Targets `netstandard2.1`. The output DLL is auto-copied to the configured deploy directory by the `CopyToPuckPlugins` MSBuild target.

References (resolved from `libs/`):

- `0Harmony.dll`
- `Puck.dll`, `Assembly-CSharp-firstpass.dll`
- Unity engine modules: `Core`, `Physics`, `Cloth`, `Audio`, `JSONSerialize`, `UIElements`, `UI`, `TextRendering`, `AssetBundle`
- `Unity.Netcode.Runtime`, `Unity.Collections`, `Unity.InputSystem`, `Unity.TextMeshPro`
- `DOTween`, `AYellowpaper.SerializedCollections`, `System.Memory`, `System.Text.Json`

### Asset bundles

`assets/compassets` and `assets/groin` are Unity-built AssetBundles consumed at plugin load. The `CompAssets/` directory contains the Unity project that produces them. Rebuild via *Assets > Build CompAssets Bundle* in Unity. The build target then copies the result to the deploy folder.

## Configuration

### Server config

Path: `Puck/Plugins/CompetitiveAdjustments/CompetitiveAdjustments.json`. Single nested JSON with three sections:

- `Dashfall`. Movement, dive, dash, stamina, feature flags ([DashfallConfig](src/ServerConfig.cs)).
- `CompAdjust`. Arena, goals, sticks, torso, ball mode, free blade, etc. ([CompAdjustConfig](src/ServerConfig.cs)).
- `CompTweaks`. Physics tuning (turns, accel, max speed, drag) ([CompTweaksConfig](src/ServerConfig.cs)).

JSON line comments (`// ...`) are stripped on load.

### Client config

Per-user file owned by `DashFallMod.Client.DashFallConfigLoader`. The toggle UI lives in the in-game ModMenu and writes back on every change. Notable client-side options:

- `EnableMinimapTweaks`. Applies arena-scale to minimap. Auto disabled on vanilla servers.
- `FreeBladeSpinLockEnabled`, `FreeBladeSpinMin`, `FreeBladeSpinMax`. Clamp blade spin to a custom range. Off equals vanilla wraparound at +/-127.
- `ShowCustomTorsoMesh`, `EnableSprintShoulderTrail`, `ShowArenaClipBrushes`, `ShowPlayerClipBrushes`. Visual preferences.

### Server to client sync

When a client connects, `DashFall.ServerBridge` sends `PPKB/GoalTweaks` (arena and goal config) and `Tweaks.PluginCore` sends `CPT_sync_config` (physics plus companion-visible flags) via Netcode `CustomMessagingManager`. Receipt of `PPKB/GoalTweaks` flips `_hasSyncedTweaks=true` on the client. This is the single gate that authorises the client to apply server-side state. Clients without this gate stay fully vanilla.

## Vanilla server safety

The plugin actively detects "joined a server without this mod" and stays inert:

- **Bounds patch** ([src/ArenaTweaks.cs:1572](src/ArenaTweaks.cs#L1572)). `EnsurePatched` is never reached; no Harmony prefix on `EncodeSynchronizedObject`/`DecodeSynchronizedObjectData` is installed. One-shot diagnostic log on first skip.
- **Arena visuals, goal scaling, audio reverb** ([src/GoalNetTweaks.cs:295](src/GoalNetTweaks.cs#L295)). `RefreshAll` distinguishes "host using local config" from "unsynced client" and forces both `enabled` and `arenaEnabled` to `false` in the unsynced-client case.
- **Minimap** ([src/DashFall.ClientRunner.cs:233](src/DashFall.ClientRunner.cs#L233)). Routes through `GoalNetTweaks.TryGetEffectiveArenaScale`, which returns `false` when not synced and a non-host. The minimap tracks the world ground plane (X = width, Z = length). Because the arena prefab carries a default 90 degree X rotation (`ArenaRotX = 90`), the config field `ArenaScaleY` scales world-Z length and `ArenaScaleZ` scales vertical height, so the minimap length is driven by `ArenaScaleY` (not `ArenaScaleZ`).

`OnClientStopped` clears the synced flag and tears down the bounds patch so the next connection starts from a clean state.

## License

No license file is checked in.

## Authors and credits

See git history. The chunked sync design was adapted from the OpenWorldPracticeMod authors.
