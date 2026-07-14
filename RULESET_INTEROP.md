# Ruleset mod interop contract (CompetitiveAdjustments x oomtm450 Ruleset)

Notes for coordinating arena resizing between this mod (CompetitiveAdjustments / "COMPADJUST")
and oomtm450's Ruleset mod, so a resized rink keeps correct offside/icing/penalty zones and
barrier behaviour on dedicated servers.

## How the two mods talk

CompetitiveAdjustments broadcasts a Unity `EventManager` event every time the arena config
changes or a player spawns:

```
EventManager.TriggerEvent("Event_CompetitiveAdjustments_OnArenaSync", message);
```

`message` is a `Dictionary<string, object>` with these keys (all floats, serialized via
`ToString`):

```
ArenaScaleX, ArenaScaleY, ArenaScaleZ, ArenaOffsetX, ArenaOffsetY, ArenaOffsetZ
```

The Ruleset mod already subscribes to this in `Event_CompetitiveAdjustments_OnArenaSync`
(Ruleset.cs) and rescales `ZoneFunc.ICE_X_POSITIONS` / `ICE_Z_POSITIONS`, calls
`PenaltyModule.Scale*/Offset*Coordinates`, and calls `LowerBarriers`.

## Axis mapping (authoritative)

The value semantics are world-space, matching what the Ruleset mod already assumes:

1. `ArenaScaleX` scales world X (rink width). Multiply the ice X positions (blue paint) by it.
2. `ArenaScaleY` scales world Z (rink length). Multiply the ice Z positions (blue lines,
   center line, goal lines, hash marks) by it.
3. `ArenaScaleZ` scales world Y (rink height). Used only for the barrier height (see below).
   CompetitiveAdjustments deliberately keeps rink height at base for gameplay, so in practice
   this is a visual-only axis and the real collision height does not change.
4. Offsets are world-space translations, applied after scaling (do not scale the offset).

The Ruleset mod's current mapping is correct: it reads `ArenaScaleX` into its X scale,
`ArenaScaleY` into its Z scale, and `ArenaScaleZ` into its Y (barrier) scale. No change needed
there.

## What changed on the CompetitiveAdjustments side (fixed)

Previously the OnArenaSync message multiplied the width and length by an internal
barrier-collider inset factor of `0.8` before sending. That shrank the Ruleset zone lines by
about 20 percent even at `ArenaScale 1.0`, because the Ruleset's `if (value == 1) break;`
early-out never fired. This is now fixed: CompetitiveAdjustments sends the raw config arena
scale and offset, which match the actual resized collision rink. No Ruleset change is required
for this, but the Ruleset zones will now line up correctly once both mods are updated.

## Issues to fix on the Ruleset side

### 1. `LowerBarriers` inverts when the height scale increases

`LowerBarriers` sets:

```
barrierCollider.transform.position.y = (boardWindowsDefaultHeight * arenaScaleY) + arenaOffsetY;
// boardWindowsDefaultHeight = -20.4, arenaScaleY here = ArenaScaleZ (height)
```

Because `boardWindowsDefaultHeight` is negative, increasing the height scale drives the
`Barrier Collider` further down, which reads to the user as "the collider moves the opposite
direction when I raise the arena." Since CompetitiveAdjustments keeps the real rink height at
base (world Y is not scaled), the barrier's vertical geometry is unchanged by a resize, so the
scale multiply is not needed here. Recommended fix, one of:

1. Drop the `* arenaScaleY` multiply and lower the barrier by a fixed amount plus the offset:
   `y = boardWindowsDefaultHeight + arenaOffsetY;`
2. Or, if you want it to track our height in the future, anchor to the barrier's current Y and
   subtract a fixed drop rather than multiplying a negative constant by a scale.

### 2. `ArenaOffsetY` handler passes the wrong value

In the offset loop, the `ArenaOffsetY` case calls:

```
PenaltyModule.OffsetYCoordinates(_arenaOffsetX);   // passes X, not Y
```

This should pass `_arenaOffsetY`.

### 3. Stale collider-hierarchy assumption

The commented-out block in `LowerBarriers` walks for a child named `CustomArenaAndColliders`
with a `Colliders` sub-object and moves `front/back/left/right/barrier` colliders. That
hierarchy no longer exists. CompetitiveAdjustments moved to a hybrid model:

1. Collision now comes from the REAL base-game colliders. We scale the gameplay scene root
   (vanilla `Level Default`, or the custom-scenery arena root on modded servers) by
   `(ArenaScaleX, 1, ArenaScaleY)`, which resizes the real ice, boards, goals and spawn
   markers in world space. Height (world Y) is left at base.
2. The bundled arena prefab is now visual only. Its `Colliders` child is stripped at spawn, so
   there is no `CustomArenaAndColliders/Colliders` tree to find.

So the object the Ruleset should manipulate is the game's real `Barrier Collider` (which is
what the active `GameObject.Find("Barrier Collider")` path already does). It is already resized
on world X/Z by our Level Default scaling; only its height handling (issue 1) needs attention.

## Server gate (informational)

The Ruleset handler early-returns unless `ServerFunc.IsDedicatedServer()`
(`SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`, i.e. headless). That is why these
interactions only appear on dedicated servers and never when a player hosts a listen server
locally. This is expected for server-authoritative rules and does not need to change; just be
aware that all of the above only runs headless.
