# Ruleset mod interop contract (CompetitiveAdjustments x oomtm450 Ruleset)

Notes for coordinating arena resizing between this mod (CompetitiveAdjustments / "COMPADJUST")
and oomtm450's Ruleset mod, so a resized rink keeps correct offside/icing/penalty zones and
barrier behaviour on dedicated servers.

## How the two mods talk

CompetitiveAdjustments broadcasts a Unity `EventManager` event when the arena changes:

```
EventManager.TriggerEvent("Event_CompetitiveAdjustments_OnArenaSync", message);
```

### When it fires (BREAKING, 2026-07-31)

It used to fire on **every `PlayerBodyV2` spawn**. It no longer does. A player spawning is not
an arena change, and because the Ruleset handler clears its `_barriersLowered` latch on entry
and re-derives the barrier collider's world Y from the message, every join and every respawn
was resetting the barrier on a dedicated server. That is the "the barrier collider disappears,
respawning brings it back" report.

It now fires only when there is something new to say:

1. the broadcast values actually changed (config edit, admin edit, or a fresh `PPKB/GoalTweaks`
   sync from the server),
2. a level spawned (`Event_Everyone_OnLevelSpawned`), which invalidates any subscriber state
   derived from the previous level, or
3. an explicit re-announce, currently the `GamePhase.PreGame` transition.

Subscribers should treat each broadcast as authoritative and idempotent. Nothing is lost by
re-applying on one you have already seen, and the level-spawn broadcast is the one to hang
per-level setup off.

### Ordering guarantee (new, 2026-07-31)

The broadcast is now sent **after** the rink has been resized, not before. This matters for any
subscriber that turns the values into an absolute world position on an object parented under the
level root, which `Barrier Collider` is.

`LowerBarriers` assigns `barrierCollider.transform.position.y`, and Unity stores a world write as
`local = world / parentScale`. Sent before `ScaleLevelDefaultRoot` ran, that write landed against
a rink still at vanilla scale and was then multiplied by the arena height scale a moment later,
putting the barrier nowhere near the boards. The level-spawn path was the worst case, because it
restores the level root to vanilla before re-applying the resize.

The rink is therefore guaranteed to be at its final scale and offset by the time the handler
runs, so reading live geometry and writing absolute world positions are both safe.

`message` is a `Dictionary<string, object>` with these keys:

```
ArenaScaleWorldX, ArenaScaleWorldY, ArenaScaleWorldZ, ArenaOffsetX, ArenaOffsetY, ArenaOffsetZ
```

**BREAKING, 2026-07-31: the `ArenaScaleX` / `ArenaScaleY` / `ArenaScaleZ` keys are gone.** Read
`ArenaScaleWorldX` / `ArenaScaleWorldY` / `ArenaScaleWorldZ` instead. Each names the WORLD axis
it scales, so there is nothing left to misread:

```
ArenaScaleWorldX   world X, rink width      (was ArenaScaleX)
ArenaScaleWorldY   world Y, rink height     (was ArenaScaleZ)
ArenaScaleWorldZ   world Z, rink length     (was ArenaScaleY)
```

The old keys were named after config fields rather than axes, and since Y and Z were swapped in
that naming they read as "Z scales the height", which is exactly the ambiguity that cost an
evening of debugging. The swap came from a bundled arena prefab rotated 90 degrees so its local
Z pointed up; that prefab is deleted, the config fields were renamed to real world axes in
ConfigVersion 16, and the duplicate keys are now removed rather than left to confuse the next
reader.

Offsets keep their names. They were world axes all along and were never swapped.

Every value is a number pre-formatted as an invariant-culture `string`. It used to be a boxed
`float`, which the Ruleset reads as `kvp.Value.ToString()` and parses with
`CultureInfo.InvariantCulture`. Boxing a float means that `ToString()` runs under the SERVER's
culture, so a fr-FR host emitted `"1,25"` and invariant parsing read the comma as a group
separator and got `125`. Sending an invariant string makes the round trip independent of the
host locale and needs no change on the Ruleset side, since `ToString()` on a string is the
string.

The Ruleset mod already subscribes to this in `Event_CompetitiveAdjustments_OnArenaSync`
(Ruleset.cs) and rescales `ZoneFunc.ICE_X_POSITIONS` / `ICE_Z_POSITIONS`, calls
`PenaltyModule.Scale*/Offset*Coordinates`, and calls `LowerBarriers`.

## Axis mapping (authoritative)

The value semantics are world-space, matching what the Ruleset mod already assumes:

1. `ArenaScaleWorldX` scales world X (rink width). Multiply the ice X positions (blue paint) by
   it.
2. `ArenaScaleWorldZ` scales world Z (rink length). Multiply the ice Z positions (blue lines,
   center line, goal lines, hash marks) by it.
3. `ArenaScaleWorldY` scales world Y (rink height). `ScaleLevelDefaultRoot` scales the level
   root by `(width, height, length)`, so the physical rink genuinely gets taller: boards, glass,
   ceiling and their colliders together.

   **BREAKING, 2026-07-31:** this used to be pinned to `1.0` on the wire no matter what the
   server had configured. It now sends the real value. The pin existed because `LowerBarriers`
   multiplies the constant `-20.4` by this scale, and since that constant is NEGATIVE, a taller
   arena drives the barrier collider FURTHER DOWN instead of leaving it alone. That is issue 1
   below and it now needs fixing on the Ruleset side, because a subscriber cannot compensate for
   a height it is never told about. Sign-guard the constant, or apply the scale to the barrier
   HEIGHT rather than to its (negative) depth.
4. Offsets are world-space translations, applied after scaling (do not scale the offset).

The Ruleset mod's axis understanding was always correct; only the key names have moved. Its X
scale now comes from `ArenaScaleWorldX`, its Z scale from `ArenaScaleWorldZ`, and its Y (barrier)
scale from `ArenaScaleWorldY`, which is the field it already calls `_arenaScaleY`.

## What changed on the CompetitiveAdjustments side (fixed)

Three fixes, none of which require a Ruleset change.

### The `0.8` barrier-inset factor

Previously the OnArenaSync message multiplied the width and length by an internal
barrier-collider inset factor of `0.8` before sending. That shrank the Ruleset zone lines by
about 20 percent even at `ArenaScale 1.0`, because the Ruleset's `if (value == 1) break;`
early-out never fired. CompetitiveAdjustments now sends the raw config arena scale and offset,
which match the actual resized collision rink.

### The height axis was pinned to `1.0` (no longer, as of 2026-07-31)

`LowerBarriers` sets an absolute world Y:

```
barrierCollider.transform.position.y = (boardWindowsDefaultHeight * arenaScaleY) + arenaOffsetY;
// boardWindowsDefaultHeight = -20.4, arenaScaleY here = our ArenaScaleZ
```

Because `boardWindowsDefaultHeight` is negative, a larger height scale drives the
`Barrier Collider` FURTHER down, which reads as "the collider moves the opposite direction when
I raise the arena."

CompetitiveAdjustments used to send `ArenaScaleZ = 1.0` unconditionally rather than ask for a
code change, which made `LowerBarriers` collapse to the intended `y = -20.4 + arenaOffsetY` at
every arena size. Verified against Ruleset `dev` (`ec57572`): the field fed from the
height key (`_arenaScaleY` on their side) is read by nothing except the three `LowerBarriers`
calls, so pinning it had no other effect.

That pin is now removed, because it stopped being true. Height used to be visual only, with the
level root scaled by `(width, 1, length)`; it is now scaled by `(width, ArenaScaleZ, length)`
and the barrier, glass and ceiling colliders really do grow. Reporting `1.0` was describing a
rink that no longer exists, and it cost the Ruleset author an evening of debugging against a
number we knew was wrong.

The fix belongs on the Ruleset side now. Either sign-guard the constant:

```
float heightScale = arenaScaleY;                     // fed from ArenaScaleWorldY
barrierCollider.transform.position.y = (boardWindowsDefaultHeight * heightScale) + arenaOffsetY;
```

so that a LARGER scale raises the barrier, or apply the scale to the barrier height above the
ice rather than to its negative depth below the origin. `ArenaScaleWorldY` is the key to read.

Until that lands, a server running a non-default `ArenaScaleZ` will have its barrier lowered to
a height computed for a rink that is not the one being played on. At the default `ArenaScaleZ`
of `1.0` the formula is unchanged, so nothing regresses for the servers that do not touch the
height knob.

### Invariant-culture number formatting

See the payload note above.

## Issues to fix on the Ruleset side

Both still present on `dev` (`ec57572`), and both are independent of the `ArenaScaleZ` change
above: they were already wrong while the height was pinned to `1.0`.

### 1. `ArenaOffsetY` handler passes the wrong value

In the offset loop, the `ArenaOffsetY` case calls:

```
PenaltyModule.OffsetYCoordinates(ArenaOffsetX);   // passes X, not Y
```

This should pass `ArenaOffsetY`.

### 2. `LowerBarriers` is called inside the offset loop, so it reads a stale `ArenaOffsetY`

`LowerBarriers(_boardWindowsDefaultHeight, _arenaScaleY, ArenaOffsetY)` sits inside the
`foreach` over the message, not after it, and `LowerBarriers` latches on `_barriersLowered`.
The first iteration of that loop handles the `ArenaScaleX` key, matches no `case`, and then
calls `LowerBarriers` anyway. That first call is the only one that does anything, and at that
point `ArenaOffsetY` has not been assigned yet this pass, so the barrier is lowered using the
PREVIOUS sync's Y offset. The handler resets `_barriersLowered = false` on entry, so this
repeats on every sync: the barrier height is always one config change behind.

Moving the `LowerBarriers` call to after the closing brace of the offset loop fixes it. The
practical impact is small at the default `ArenaOffsetY` of about 1 cm, and only becomes visible
on a server that sets a real Y offset.

### 3. Stale collider-hierarchy assumption

The commented-out block in `LowerBarriers` walks for a child named `CustomArenaAndColliders`
with a `Colliders` sub-object and moves `front/back/left/right/barrier` colliders. That
hierarchy no longer exists, and leaving the block commented out is correct.

1. Collision comes from the REAL base-game colliders. We scale the gameplay scene root
   (vanilla `Level Default`, or the custom-scenery arena root on modded servers) by
   `(ArenaScaleX, ArenaScaleZ, ArenaScaleY)` in world XYZ, which resizes the real ice, boards,
   goals and spawn markers in world space. Height rides `ArenaScaleZ` along with the rest,
   which is what the payload note in point 3 above is warning about.
2. The bundled arena prefab and every other AssetBundle were deleted on 2026-07-30, so there is
   no `CustomArenaAndColliders` node in any scene at all.

So the object the Ruleset should manipulate is the game's real `Barrier Collider` at
`Level Default/Rink/Barrier Collider`, which is what the active `GameObject.Find("Barrier
Collider")` path already finds. CompetitiveAdjustments never renames, reparents, disables or
clones it; it is only carried along by the level root's scaling, so it arrives at the right
width and length on its own. Two robustness notes on that `Find`:

1. `GameObject.Find` searches by name across every active object in the scene and returns an
   arbitrary match. Custom scenery scenes (the Ponce dedicated arena, for instance) have already
   broken our own arena-root lookup this way. Scoping the search under the level root would be
   safer.
2. The result is dereferenced without a null check. It is inside the `try`, so a missing node
   logs an exception rather than crashing, but the latch is not set on that path so it retries,
   which is fine.

## Server gate (informational)

The Ruleset handler early-returns unless `ServerFunc.IsDedicatedServer()`
(`SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`, i.e. headless). That is why these
interactions only appear on dedicated servers and never when a player hosts a listen server
locally. This is expected for server-authoritative rules and does not need to change; just be
aware that all of the above only runs headless.
