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
- Stick spin fatigue: a speed limit on the blade, earned by spinning it.
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

### Stick spin fatigue

Nothing in the game limits how fast the blade turns. `PlayerInput` clamps the angle's RANGE and the mod's own spin lock replaces that clamp with a wrap, but the rate is whatever the scroll wheel emits. A wheel built on a bearing rather than a notched detent emits far more of those events than a hand can click, so it can hold the blade in a permanent full-speed spin. `StickSpinFatigueEnabled` (on by default, in `CompAdjust`) caps the blade's angular speed once a player has turned it a whole revolution one way inside `StickSpinFatigueWindowSeconds`.

The cap lasts `StickSpinFatigueSlowSeconds` from the last fast revolution, and stacks: the first is `StickSpinFatigueLimitDegreesPerSecond`, each further one multiplies by `StickSpinFatigueStackFactor` up to `StickSpinFatigueMaxStacks`, which at the defaults is 360, 216, 130 then 78 degrees per second. Stacks are earned on what the player ASKS for rather than on what the cap lets through, so keeping the wheel spinning keeps deepening it; they clear together once the slowdown has been over for `StickSpinFatigueRecoverySeconds`.

Everything is measured in degrees of blade rotation, not wheel notches, so the binding's own scale is irrelevant: one unit of `BladeAngleInput` is `Stick.bladeAngleStep` degrees (12.5 on the stock prefab), which puts a revolution at 28.8 units. The default trigger therefore asks for 57.6 scroll steps per second sustained across half a second, which a notched wheel cannot reach, and the whole feature is inert unless `FreeBladeEnabled` is on, since vanilla's +/-4 range cannot hold a revolution.

It runs on both ends ([StickSpinFatigue.cs](src/StickSpinFatigue.cs)). The client limits its own input, which is what everyone sees, because the blade pose comes from the owner's value relayed back through the server. The server then re-applies the limit to the value each client asks it to relay, so a client with the limiter removed gains nothing; that half runs a deliberately looser cap and should never be the one that bites. Set `StickSpinFatigueServerEnforced` to false to leave the rule entirely to the clients.

The enable flag rides a spare bit of `ConfigSyncPackage.BoolFlags` rather than only the `PPKB/ConfigFull` JSON, which is what makes it behave on a server older than the feature. `FromJsonOverwrite` leaves a field the server never sent at whatever the client already had, so carrying it in the JSON alone would have left a client on an old server limiting itself while nobody else was. A server too old to set the bit sends 0, and 0 is the right answer.

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
  StickSpinFatigue.cs           blade speed limit earned by spinning, client and server halves
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
  ModMenuHub.cs                 shared Ponce mod-menu hub, no longer used by this mod (see below)
  ServerConfig.cs               nested JSON config (Dashfall / CompAdjust / CompTweaks)
  DashFall.Theme.cs             shared design system: palette, geometry, widget factories
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

Per-user file owned by `DashFallMod.Client.DashFallConfigLoader`. The toggle UI writes back on every change.

**Press F4 to open it.** The key is hardcoded and toggles the panel; ESC closes it. There are no on-screen buttons: the panel used to be reached through the shared Ponce mod-menu hub, and that entry point was removed, so [ModMenuHub.cs](src/ModMenuHub.cs) is still in the tree only because the other Ponce mods ship the same file. This mod no longer registers with it, which means its copy can never become the hub's primary runner. F4 was chosen because the rest of the family already had its own key: OWP is F1, MOTD is F2, MaxPractice is F3.

Notable client-side options:

- `FreeBladeSpinLockEnabled{Skater,Goalie}`, `FreeBladeSpinMin{Skater,Goalie}`, `FreeBladeSpinMax{Skater,Goalie}`. **Per role since client config version 2**, edited on the SKATER and GOALIE tabs rather than in SETTINGS, because a goalie holding an angle across the crease and a skater carrying the puck are not asking the same thing of the blade. Which pair applies is decided from `Player.Role` on the input itself, so a position change takes effect on the next input. An older config is migrated once by copying its single pre-split setting onto both roles, so a deliberate choice is not reset. **Both roles are on by default at +/-4, which is vanilla's own blade range**, so a stock client plays like vanilla even on a server running FreeBlade. Free spin is opt-in: turn the lock off, or widen the range with the two-handled slider, which reaches +/-127. Because the default range is exactly vanilla's limit, leaving the lock on means FreeBlade has no visible effect. That is intended, not a bug; it is the same behaviour once reported as "the blade locks at max twist", now a stated default with a switch next to it.

  The two settings are not the same knob at different strengths. Widening the range still clamps, and +/-127 is as far as it goes, because `BladeAngleInput` travels the wire as an `sbyte`; at 12.5 degrees per step that is about four and two fifths turns from centre and then the blade stops. Turning the lock **off** wraps the value instead, so the blade turns without end. The rollover is 144 steps, exactly five turns, so it lands on the same pose and nothing jumps on screen (`BladeSpinWrap` in [StickAnglePatch.cs](src/StickAnglePatch.cs)).

  Wrapping does cost something on clients that do not have the mod, and it is worth knowing before turning the lock off. They clamp whatever they receive to vanilla's +/-4, so under the old behaviour, where the value ran to 127 and stopped, they saw the blade sit steady at +4. Under wrapping the value sweeps the whole period, so they see it hold at one bound, flip to the other, and sweep back, once per rollover. Modded clients see the real spin.

  Endless is not the same as unlimited. Neither setting bounds how FAST the blade turns, which is the part a free spinning wheel exploits, and that is what stick spin fatigue above limits. It is a server setting, so the lock and the range stay the client's own choice.
- `EnableSprintShoulderTrail` plus the trail time, width, colour and opacity fields. Colour and opacity are edited together in one picker row.
- `EnableClientDebug`. Turns on debug logging and reveals the debug-only rows below it (`ShowArenaClipBrushes`, `ShowPlayerClipBrushes`, the version-popup preview). Turning it off also retracts any clip brushes that were showing.

The minimap is always rescaled to match the arena and has no toggle: the map normalises dots against `UIMinimap.Bounds`, which follows the arena scale, so a vanilla minimap on a resized rink simply puts the dots in the wrong place. It is a no-op on a vanilla rink and on a vanilla server.

Puck scale and the goalie leg pad offset are server state. They live in the client config as sync slots only, are not persisted, and have no settings rows.

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
