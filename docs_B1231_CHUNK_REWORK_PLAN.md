I have verified enough to decide. Writing the plan.

---

# Implementation plan: long-range network positions on Puck B1231

## 0. Verification I performed before choosing (this changes the outcome)

Every claim below was re-derived from `C:\Program Files (x86)\Steam\steamapps\common\Puck\Puck_Data\Managed\Puck.dll` and `libs\Unity.Netcode.Runtime.dll` with Mono.Cecil. Dumper at `C:\Users\Amiki\AppData\Local\Temp\claude\c--Users-Amiki-OneDrive-Desktop-Desk-Development-ComptitiveAdjustments-B310\8e970427-b2ad-43a4-915e-b9aeb1c6fe1c\scratchpad\q.ps1`.

**The single most important finding: the "host self-echo" attack that was rated FATAL against two of the three designs is built on a false premise, and I falsified it directly.**

The attack asserted that `Server_AddSynchronizedPlayer` has no host filter, so a listen host round-trips its own objects through `Write`/`Read` and writes decoded positions onto its own authoritative transforms. The filter is not in that method, it is in its caller, and there is exactly one caller:

```
SynchronizedObjectManagerController::Event_Everyone_OnPlayerSpawned
  IL_002d callvirt System.UInt64 NetworkBehaviour::get_OwnerClientId()
  IL_0032 brtrue.s IL_0035          // non-zero id continues
  IL_0034 ret                        // OwnerClientId == 0 (the host) returns here
  IL_003c callvirt SynchronizedObjectManager::Server_AddSynchronizedPlayer(Player)
```

The late-join seed path is gated identically:

```
SynchronizedObjectManagerController::Event_Server_OnClientSceneSynchronizeComplete
  IL_0012 brtrue.s IL_0015
  IL_0014 ret                        // clientId == 0 returns here
  IL_001c callvirt SynchronizedObjectManager::Server_ForceSynchronizeClientId(System.UInt64)
```

A caller sweep confirms `Server_AddSynchronizedPlayer` has that one caller and that `serverSynchronizedPlayerStates` is written only there. So a listen host never gets a `SynchronizedPlayerState`, never gets an `RpcTarget`, and never receives its own synchronization stream. `Client_ApplyReliableSynchronizedObjectData` is unreachable on a host. The whole runaway-divergence chain fails at step one.

That verdict was the only fatal attack on the chunk-word design and the only fatal attack on the residue design. Both are void.

Other claims I confirmed, because the briefing was wrong twice and I built on these:

1. **Wire order.** `Write` and `Read` are 0x208-byte mirrors. Order is `NetworkObjectId` (unconditional, IL_0003), `ChangeMask` (unconditional, IL_0019), `X`&1, `Y`&2, `Z`&4, then `&8` selecting either four Int16 (`&1024` set) or one UInt32, then `Vx/Vy/Vz` on 16/32/64, `Ax/Ay/Az` on 128/256/512, and **`TickRateDivisor` LAST on bit 2048** at IL_01f4. Briefing item 4 is wrong; TickRateDivisor is not the third field.
2. **Free mask bits.** A whole-assembly `ldc.i4` scan for 8192 returns one hit, `ApplicationManager::SetShadowQuality`. 16384 and 32768 return nothing. 4096 is live (`get_IsAsleep` IL_0006, `WithAsleep` IL_0010) and 2048 is a live wire gate. Briefing item 6 is wrong. Only bits 13, 14, 15 are free.
3. **Spare bits are erased twice.** `WithComponentMask` IL_000a does `& 3071`; `Merge` IL_0166 forces `3071 | (x & 1024)`. A flag exists only between a `Write` prefix and a `Read` postfix.
4. **Encode point.** The ctor sets `NetworkObjectId` at IL_0003 and `ChangeMask = 4095` at IL_000e, then reads `position` (ldarg.2) at exactly IL_0014, IL_002f, IL_004a. A prefix taking `(ulong networkObjectId, ref Vector3 position)` fully controls what is compressed.
5. **Decode point.** `GetPosition` has exactly four call sites: `GetChangeMask` IL_0001 and IL_0008 (both operands, so a per-id offset cancels), plus `BufferObjects` IL_003b and `ReceiveReliable` IL_0057.
6. **The asleep trap is real.** `IsFullSendDue` returns false at IL_0012 when `!IsAwake`. `PlanSend` returns at IL_005d when `(mask & 1023) == 0 && !IsAwake`. A sleeping object receives nothing, ever, and has no repair path.
7. **The MTU failure is a throw, not a drop.** The two attacks contradicted each other here. `NetworkBehaviour::__endSendRpc` compares `writer.Length` against `NetworkMessageManager::NonFragmentedMessageMaxSize` at IL_007d and throws `OverflowException("RPC parameters are too large for unreliable delivery.")` at IL_008e, before the send and before `Dispose` at IL_0246. Since `Server_Tick` wraps the whole per-player loop in one try/catch, one oversized player kills the sync tick for every player after it in the list and leaks the Temp writer.

I also found that the old chunk files are **not** dead code as one attack claimed. `ArenaTweaks.cs:1167/1190/1197` calls `NetworkBoundsPatch`, `ArenaTweaks.cs:1220-1229` calls `ChunkRegistry.ChunkSizeMeters`, and `GoalNetTweaks.cs:368` calls `ChunkSyncClient.TickRegistrationRetry()` on the 1 Hz tick. Only `SyncRangePatch` has no callers.

Finally, a latent bug worth flagging now: `ArenaTweaks.cs:1203-1204` declares `VanillaArenaHalfExtentX = 50f` and `VanillaArenaHalfExtentZ = 25f`, which is the opposite of the wire, where X is the narrow axis at ±25 and Z is the long axis at ±50. One of the two is wrong. It has never mattered because it only feeds a log line, but the new code must not inherit it.

## 1. Design decision

**Adopt Design 1, the flag-gated trailing chunk word,** with four mandatory fixes described in section 3. I am not manufacturing a winner; with the host premise falsified, this design has no surviving fatal attack, and the two open issues against it were both rated fixable and are fixed below.

Why not the other two.

Design 3, global origin rebase or static scale, is dead on arithmetic and I confirmed the counting independently. Reaching 400 m of span at vanilla's 0.763 mm X step needs about 524,000 codes, roughly 19 bits, against the 16 the field holds. A global offset conveys zero bits per object, and since the rink is centred on world origin the optimal offset is zero anyway. A per-region offset is per-object chunking renamed. A static scale is provably the same function as widening the literals, because `InverseLerp(-R, R, k*w)` equals `InverseLerp(-R/k, R/k, w)` identically, so it is `SyncRangePatch` with the multiply on a different line. Geometry-side scaling conserves the same precision loss in gameplay units and adds a PhysX retune. Reject.

Design 2, modulo residue with phase unwrap, is genuinely viable now that its fatal attack is void, and it has a real attraction: zero wire bytes, so no version gate can ever shred a packet stream. I am rejecting it on one property. Vanilla's behaviour under packet loss is "stale but correct". Residue unwrap converts that into "wrong by a whole period, permanently, until an anchor lands", and the threshold is per-received-sample displacement exceeding P/2. That threshold is a function of the LOD and culling `TickRateDivisor` values, which are serialized MonoBehaviour fields that no static analysis can read, so the safety margin is literally unmeasurable before shipping. On top of that, the anchor repair channel as specified is algebraically wrong: computing `m = P*round((C - v(T))/P)` against the client's own history makes the repair depend on the chain it is repairing, and it can displace a perfectly healthy object by a full period depending only on where the anchor's timestamp falls inside a loss gap. Fixable, but the result is a design whose correctness rests on tuning an anchor rate against a max-speed assumption. The chunk word keeps the short and its origin in the same sixteen bits of the same record, so pairing is atomic by construction and loss degrades exactly the way vanilla degrades.

Keep Design 2 written down as the fallback if step 4 of the rollout shows the version gate cannot be made safe in practice.

## 2. The design in one paragraph

Keep the vanilla ranges and their literals untouched, so precision stays bit-identical at 0.763 mm on X and 1.526 mm on Y and Z. Give each object a per-axis chunk index chosen once per tick on the server. Subtract `index * chunkSize` in a ctor prefix before quantisation, add it back in a `GetPosition` postfix after dequantisation. Carry the three indices in one fixed-width UInt16 appended after the vanilla record, gated by `ChangeMask` bit 13 (0x2000) so it costs nothing when everything is at chunk zero. Parse it in a `Read` postfix but commit it in a `TryMergeReceivedData` postfix, under the same per-axis mask bits that gate the shorts.

## 3. The four mandatory fixes over the design as written

1. **The join-window seam.** `Server_ForceSynchronizeClientId` fires from the scene-sync-complete handler, which is strictly before the client's `PPKB/ClientVersion` announce can arrive, because `NetworkSceneManager` sends SynchronizeComplete before setting `IsConnectedClient`, and `ClientVersionCheck.cs:162` gates the announce on `IsConnectedClient`. So the seed packet is always sent while the client is classified unsupported, and `ClientVersionCheck.cs:88` gives a 5 second grace on top. Any object that is stationary at that moment goes to sleep in the unsupported encoding and, per finding 6, is never sent again. Fix: on the transition from unsupported to supported for a client, wipe that player's entire send baseline. Verified this works: `IsFullSendDue` checks `!HasLastSentData` at IL_0006 and returns true at IL_0008 **before** it consults `IsAwake` at IL_000b, so a wiped baseline forces mask 3071 for every object including sleepers, `(3071 & 1023) != 0` diverts `PlanSend` to IL_0081, and everything is re-sent complete on the reliable channel. This one fix closes both the join seam and the asleep-object trap.
2. **Do not seed the gate from a build number alone.** `ClientVersionCheck` announces `SharedConstants.MOD_BUILD`, a compile-time constant with no relationship to whether the chunk patches actually installed. A client whose patches failed to install would announce "supported" and then never consume the appended bytes, which shreds every element of every array. Announce a separate capability bit set only after all four chunk patches verifiably resolved and applied.
3. **Force the axis bits on a chunk change.** `GetChangeMask` compares chunk-local floats at a 0.002 m threshold, so a teleport of exactly one chunk size on an axis (spawn placement, faceoff reset, arena rebuild) leaves the local coordinate unchanged, clears the bit, and the client keeps the old origin. Stamp a force window on any chunk change and OR bits 1|2|4 in a `GetChangeMask` postfix while it is open.
4. **Budget the MTU against a throw.** Per finding 7 this is not a per-player drop. Compute the per-tick payload ceiling and keep object count under it, or raise `NetworkManager.MaximumTransmissionUnitSize`. Working numbers below.

## 4. Harmony patches

All targets are on non-public members of value types, so every `__instance` must be declared `ref SynchronizedObjectData __instance`. Install under a dedicated Harmony id, separate from the lifecycle and tweaks harmonies, so it can be unpatched independently.

### 4.1 `SynchronizedObjectSnapshot.Capture(SynchronizedObjectRegistry registry)`

Resolve: `AccessTools.Method(typeof(SynchronizedObjectSnapshot), "Capture", new[]{ typeof(SynchronizedObjectRegistry) })`. Kind: **prefix**, signature `static void Prefix(SynchronizedObjectRegistry registry)`.

Body: bump the mod's own tick counter. For each object in `registry.Objects` with `NetworkObjectId <= 65535`, read its pose position and refresh the encode chunk with hysteresis: keep the current index unless `|world - index*size|` exceeds the re-chunk threshold, in which case set `index = Mathf.RoundToInt(world / size)` and stamp `forceUntilTick[id] = tick + TickRate`. Clamp indices to the field ranges and log once per id on clamp. If the kill switch is off or `GoalNetTweaks.TryGetEffectiveArenaScale` returns false, force every index to zero instead.

This is the only place the chunk is decided, so every player's records for a tick agree by construction. Verified two callers, both server-only: `Server_Tick` IL_008e and `Server_ForceSynchronizeClientId` IL_0019. Sharing state across both is what stops the late-join seed picking a different chunk from the tick path. Note `Server_Tick` returns before `Capture` when there are zero players, so the scheduler must tolerate gaps.

### 4.2 `SynchronizedObjectData..ctor(ulong, Vector3, Quaternion, Vector3, Vector3)`

Resolve: `AccessTools.Constructor(typeof(SynchronizedObjectData), new[]{ typeof(ulong), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector3) })`, the same handle `SyncRangePatch.cs:174` already uses. Kind: **prefix**, signature `static void Prefix(ulong networkObjectId, ref Vector3 position)`.

Body: look up the chunk triple for `(ushort)networkObjectId` and subtract `index * size` per axis from `position`. No `__instance`, which sidesteps the ref-struct trap entirely. Verified the only caller in the whole assembly is `Capture` at IL_00b1, so this is guaranteed server-only and runs once per object per tick.

### 4.3 `SynchronizedObjectData.GetChangeMask(SynchronizedObjectData other, bool useHighPrecisionRotation)`

Kind: **postfix**, signature `static void Postfix(ref SynchronizedObjectData __instance, ref ushort __result)`.

Body: if `forceUntilTick[__instance.NetworkObjectId] > currentTick`, `__result |= 7`. Verified single caller, `PlanSend` IL_003f. Because `PlanSend` only enters the sleep branch when `(mask & 1023) == 0`, this also wakes a sleeper that re-chunked.

### 4.4 `SynchronizedObjectData.Write(FastBufferWriter)` prefix

Resolve: `AccessTools.Method(typeof(SynchronizedObjectData), "Write", new[]{ typeof(FastBufferWriter) })`. Kind: **prefix**, signature `static void Prefix(ref SynchronizedObjectData __instance, FastBufferWriter writer)`.

Body, in order:

1. Read the current target client id from the static published by 4.9.
2. If that client is not chunk-capable, or the kill switch is off: for each of bits 1, 2, 4 that is set, rewrite that axis's short as `CompressFloatToShort(DecompressShortToFloat(short, vanillaMin, vanillaMax) + index*size, vanillaMin, vanillaMax)`, which saturates at the vanilla wall exactly as an unmodded server would. Leave bit 0x2000 clear, stage nothing, return.
3. Otherwise, if `(__instance.ChangeMask & 7) != 0` and any of the three indices for that id is non-zero, pack the word, stage it in a static, and `__instance.ChangeMask |= 0x2000`.

Mutating `__instance` here is safe and does not leak: `WriteNetworkSerializable<T>(T&)` does `ldarg.1; ldobj T; stloc.1; ldloca.1`, so the struct is a stack copy of the pooled array element. The vanilla body then serializes the modified mask for free at IL_0019. This is the only point at which a spare bit can reach the wire, because `PlanSend` runs every struct through `WithComponentMask`.

### 4.5 `SynchronizedObjectData.Write(FastBufferWriter)` postfix

Kind: **postfix**, same signature. Body: if `(__instance.ChangeMask & 0x2000) != 0`, `writer.WriteValueSafe(ref stagedWord)`. `FastBufferWriter`'s only instance field is `WriterHandle* Handle`, so the by-value copy shares the cursor and the append lands immediately after the vanilla `TickRateDivisor` byte.

### 4.6 `SynchronizedObjectData.Read(FastBufferReader)`

Kind: **postfix**, signature `static void Postfix(ref SynchronizedObjectData __instance, FastBufferReader reader)`.

Body: if `(__instance.ChangeMask & 0x2000) != 0`, read one UInt16, unpack the three signed fields, and stage them by `__instance.NetworkObjectId`. If the flag is clear, stage `(0,0,0)`. **Parse only, do not commit.** A prefix cannot work because `ChangeMask` is not read until IL_0019.

The "flag clear means zero, not unchanged" rule is load-bearing. Without it an object returning from chunk 1 to chunk 0 sends an X with no flag and the client keeps origin 1 forever.

### 4.7 `SynchronizedObjectClientReceiver.TryMergeReceivedData(SynchronizedObjectData, double, out SynchronizedObjectData)`

Kind: **postfix**, signature `static void Postfix(bool __result, SynchronizedObjectData synchronizedObjectData)`.

Body: if `__result` is false, do nothing. Otherwise copy the staged X into the client chunk table only if `(synchronizedObjectData.ChangeMask & 1) != 0`, Y only on `& 2`, Z only on `& 4`. This mirrors `Merge`'s own per-axis gating at IL_0007, IL_001e, IL_0035 exactly, at the same instant, so the chunk table and `receivedStates` cannot disagree.

Committing here rather than in `Read` is not stylistic. `Read` runs before every drop test in the pipeline: before `ReceiveTick`'s whole-tick out-of-order guard, before `ReceiveReliable`'s `Client_HasNewerDataThan` skip, and before this method's own strictly-greater `serverTime` test. A duplicate the receiver correctly discards would still poison the chunk table.

### 4.8 `SynchronizedObjectData.GetPosition()`

Resolve: `AccessTools.Method(typeof(SynchronizedObjectData), "GetPosition")`, the handle `SyncRangePatch.cs:178` already uses. Kind: **postfix**, signature `static void Postfix(ref SynchronizedObjectData __instance, ref Vector3 __result)`.

Body: add `index * size` per axis from the role-selected table, keyed by `__instance.NetworkObjectId`. Select the server encode table when `NetworkManager.Singleton.IsServer`, the client received table otherwise. Return unchanged when the table has no entry.

Two tables, never one static, even though the host exclusion means a host's client table stays empty. Keep the split as insurance.

Do not patch `SynchronizedObjectSnapshot.GetPosition(int)`. Different method, returns the raw un-quantised pose, feeds LOD banding and culling.

### 4.9 `SynchronizedObjectManager.Server_SynchronizePlayer(SynchronizedPlayerState)` and `Server_ForceSynchronizeClientId(ulong)`

Kind: **prefix and finalizer** on each. Body: publish and then clear the current target client id, taken from `synchronizedPlayerState.Player.OwnerClientId` and from the argument respectively. Use a finalizer rather than a postfix so the `__endSendRpc` overflow throw cannot leave the static latched.

The window is exact: `Server_SynchronizePlayer` passes the RpcTarget into the RPC at IL_0067 and the send stage serializes the whole array inline before returning, with no queueing and no re-entrancy.

### 4.10 Eviction, all postfixes, all unconditional

`SynchronizedObjectClientReceiver.Forget(ulong)` and `.Dispose()` evict and clear the client chunk table, in lockstep with `receivedStates`, which has no other eviction path. `SynchronizedObjectManager.RemoveSynchronizedObject(SynchronizedObject)` and `SynchronizedObjectRegistry.Clear()` evict and clear the server encode, hysteresis and force tables.

These must never consult the arena-scale predicate. `DashFall.ClientRunner` calls `GoalNetTweaks.ClearSyncedTweaks()` on `OnClientStopped`, which can flip that predicate false before Puck's own receiver teardown runs, and a gated cleanup would strand the table across sessions onto recycled ids.

### 4.11 Support-transition hook (not a Harmony patch)

In `ClientVersionCheck.OnClientVersionMsg`, at the moment a client first becomes chunk-capable, reflect `SynchronizedPlayerState.sendStates` for that player and `Clear()` it. Do not call `Server_ForceSynchronizeClientId` instead: that path builds its header with the current `serverTickTime`, and `TryMergeReceivedData`'s strictly-greater test would drop those records for every object the client already holds, silently no-opping in exactly the case that needs fixing.

## 5. Wire framing

Unchanged for every vanilla field. One UInt16 is appended after the vanilla `TickRateDivisor` byte, present if and only if `ChangeMask & 0x2000`. The appended length is a constant 2 bytes regardless of which position bits are set, which keeps Write/Read symmetry a one-line invariant. That matters more than the byte it costs, because `WriteNetworkSerializable` writes an Int32 element count then concatenates elements with no per-element length, so an asymmetry does not corrupt one record, it shreds every remaining element in the array.

Word layout, little-endian, all fields two's complement signed:

```
bits 0..5    chunkX   6 bits   -32..+31
bits 6..9    chunkY   4 bits    -8..+7
bits 10..15  chunkZ   6 bits   -32..+31

pack:   w = (ushort)((cx & 0x3F) | ((cy & 0x0F) << 6) | ((cz & 0x3F) << 10))
unpack: cx = ((w      ) & 0x3F ^ 0x20) - 0x20
        cy = ((w >>  6) & 0x0F ^ 0x08) - 0x08
        cz = ((w >> 10) & 0x3F ^ 0x20) - 0x20
```

Chunk sizes are compile-time protocol constants, never synced runtime values: `CHUNK_X = 40`, `CHUNK_Y = 80`, `CHUNK_Z = 80`. Re-chunk thresholds are `|local| > 22` on X and `> 44` on Y and Z, so worst-case `|local|` is 22 and 44 against windows of 25 and 50, leaving 3 m and 6 m of guard. Reach is ±1262 m on X, ±604 m on Y, ±2524 m on Z, against a requirement of ±200 m.

One byte cannot meet the requirement. Three signed fields in 8 bits gives at best 3 bits each, which is 8 chunk slots, and X's window is only 50 m wide, so the span caps near ±192 m with zero guard.

### 5.1 Worked byte-by-byte example, one object crossing a chunk boundary

Object `NetworkObjectId = 7`, moving along +X, `CHUNK_X = 40`. Both ticks are X-only deltas, so the delta mask from `GetChangeMask` is 1, `WithComponentMask` leaves it at 1, and the Write prefix ORs in 0x2000 to give 0x2001.

**Tick N. True world x = 61.8, current chunkX = 1.**

Local is `61.8 - 1*40 = 21.8`. `|21.8| < 22`, so no re-chunk.

```
CompressFloatToShort(21.8, -25, 25)
  t     = (21.8 + 25) / 50            = 0.936
  raw   = -32768 + 65535 * 0.936      = 28572.76
  X     = 28573                        = 0x6F9D
word = (1 & 0x3F) | 0 | 0              = 0x0001
```

Record on the wire, 8 bytes:

```
07 00   NetworkObjectId = 7
01 20   ChangeMask      = 0x2001   (bit 1 = X present, bit 13 = chunk word present)
9D 6F   X               = 28573
01 00   ChunkWord       = 0x0001   (cx=1, cy=0, cz=0)
```

Client decodes `DecompressShortToFloat(28573, -25, 25) = -25 + 50*(61341/65535) = 21.7998`, postfix adds `1*40`, result **61.7998**.

**Tick N+1. True world x = 62.3.**

Local against chunk 1 is `62.3 - 40 = 22.3`, which exceeds 22, so the Capture prefix re-chunks: `index = Mathf.RoundToInt(62.3 / 40) = 2`, and stamps `forceUntilTick[7] = tick + TickRate`. New local is `62.3 - 2*40 = -17.7`.

```
CompressFloatToShort(-17.7, -25, 25)
  t     = (-17.7 + 25) / 50           = 0.146
  raw   = -32768 + 65535 * 0.146      = -23199.89
  X     = -23200                       = 0xA560 as UInt16
word = (2 & 0x3F) | 0 | 0              = 0x0002
```

Record on the wire, 8 bytes:

```
07 00   NetworkObjectId = 7
01 20   ChangeMask      = 0x2001
60 A5   X               = -23200
02 00   ChunkWord       = 0x0002   (cx=2, cy=0, cz=0)
```

The short jumped from 28573 to -23200 and the chunk index from 1 to 2 **in the same eight bytes**. Nothing can separate them.

Client side: `Read` consumes id, mask, X, then sees bit 0x2000 and consumes the word, staging `(2,0,0)` for id 7. `TryMergeReceivedData` accepts on strictly-greater serverTime, `Merge` copies X because bit 1 is set, and the commit postfix writes `chunk[7].x = 2` because the same bit 1 is set. Y and Z chunks are untouched, matching the untouched Y and Z shorts. `GetPosition` returns `-25 + 50*(9568/65535) = -17.70008`, postfix adds `2*40 = 80`, result **62.29992** against a true 62.3, an error of 8.4e-5 m, well inside the 0.763 mm quantisation step.

Server-side change detection needed no help here. `GetChangeMask` decoded both operands with the same per-id offset so it cancelled, the comparison ran in chunk-local space, and 21.7998 against -17.70008 is a 39.5 m difference against a 0.002 m threshold. Bit 1 was set for every player automatically. The `forceUntilTick` stamp exists only for the teleport case where that does not happen.

**If the tick N+1 record is lost:** the client keeps `(28573, chunk 1)` and renders 61.7998. Stale but correct, exactly vanilla's loss behaviour, no teleport and no garbage. The next record touching X carries both halves again, and the per-object reliable full send rewrites all three pairs within `tickRate` ticks.

**If the client is not chunk-capable:** the Write prefix instead rewrites X as `CompressFloatToShort(-17.70008 + 80, -25, 25)`, which saturates to 32767, leaves bit 0x2000 clear, and appends nothing. That client receives a byte-exact vanilla 6-byte record and sees the object pinned at the +25 m wall, which is precisely what it would see on a vanilla server.

## 6. Bandwidth and MTU budget

Zero cost when every position axis in a record is at chunk zero, so a vanilla-sized rink is byte-identical to vanilla. When active it is a flat 2 bytes: a full send goes 27 to 29 bytes with compressed rotation, or 31 to 33 with high-precision rotation, which every asleep and force-sync record uses because the ctor sets mask 4095 including bit 1024.

The unreliable payload ceiling is `NonFragmentedMessageMaxSize = 1296`, and per finding 7 exceeding it **throws** inside `__endSendRpc` and kills the tick for every player later in the loop. Budget: `(1296 - 19 tick header - 1 null flag - 4 count) = 1272` bytes for records. That is about 47 objects at 27 bytes, about 43 at 29, about 38 at 33. Measure the worst-case object count on the target server before shipping, and treat the roughly 8 percent headroom reduction as the real cost of this feature.

One honest correction to the design's "exactly zero" bandwidth claim. Vanilla currently **suppresses** the position bits of any saturated axis, because `GetAxisChangeMask` sees two identical pinned decodes and a difference of exactly zero. Un-pinning those objects restores up to 6 bytes of position shorts per out-of-box object per tick that vanilla was not sending. That is the price of fixing the bug, not an overhead of chunking, but it must be in the MTU arithmetic.

## 7. Disposition of the existing files

1. **`src/Net/ChunkRegistry.cs`: delete.** Its `ChunkSlot.ResolveAt(tickId)` model resolves a chunk per object per tick, which was right for the old whole-position encoder and is exactly the mispairing failure under B1231's per-axis deltas. Its encode helpers are B310 fixed point (`world * 655f`) and do not apply to the Lerp/InverseLerp mapping. Its 32 m floor-based chunking puts local in `[0,32)`, which does not fit the centred `[-25,25]` X window. Nothing in it is salvageable except the idea of a `ChunkCoord` struct, which is three fields.
2. **`src/Net/ChunkSyncServer.cs` and `src/Net/ChunkSyncClient.cs`: delete.** Both resolve B310 symbols by string. Note the widely-repeated claim that they are inert because `AccessTools` returns null is wrong for at least two targets: `Server_SynchronizeObjectsRpc` and `Server_ForceSynchronizeClientId` both still exist on B1231. They are inert only because `NetworkBoundsPatch.EnableOpenWorldPrecision` calls `EnsurePatched()` first, which bails on the genuinely absent `EncodeSynchronizedObject` before reaching `ChunkSyncServer.Enable()`. That is a single early return standing between live ordnance and the exact hook the new design wants. Delete them before writing the new hook, not after.
3. **`src/Net/NetworkBoundsPatch.cs`: delete,** and replace its three live call sites. `ArenaTweaks.cs:1167` (`ChunksEnabled`), `:1190` (`EnableOpenWorldPrecision`), `:1197` (`Disable`) become calls into the new `ChunkPositionPatch`. `ArenaTweaks.LogRequiredChunks` at 1206 to 1230 is rewritten against the new per-axis chunk sizes and index ranges, and the `VanillaArenaHalfExtentX = 50f` / `Z = 25f` constants at 1203 to 1204 must be re-derived, because they are the opposite way round from the wire ranges and one of the two is wrong.
4. **`GoalNetTweaks.cs:368`**, the `ChunkSyncClient.TickRegistrationRetry()` call on the 1 Hz tick, is deleted outright. The new design uses no named-message side channel, so there is no registration to retry.
5. **`src/Net/SyncRangePatch.cs`: keep, still disabled, and add a header note.** It has no callers and never has, so none of its behaviour has ever been exercised. Keep it as the documented fallback if the version gate proves unworkable, and as the reference for the transpiler mechanics. Correct its header claim that on a resized rink "the host sees nothing wrong": with the host excluded from the sync path, the host actually sees the correct physics, which is right for a different reason than the file states.

## 8. Implementation steps, in order, each independently testable

1. **Delete the dead chunk stack and unwire it.** Remove the four files listed above, replace the `ArenaTweaks` and `GoalNetTweaks` call sites with stubs, rewrite `LogRequiredChunks` against the new constants, and fix the swapped half-extent constants. Test: the project builds clean, and a normal session on a vanilla-sized rink behaves exactly as it does today.
2. **Add `ChunkTables`, pure data, no Harmony.** Flat arrays indexed by ushort id for the server encode chunk, the hysteresis state, the force-until-tick stamp, and separately for the client received chunk. `Capture` guarantees ids stay under 65536, so a flat 65536-entry array is a total function of the key with no dictionary in the hot path. Test: unit-test pack and unpack round-trips across the full signed range of all three fields, and unit-test the hysteresis state machine against a synthetic position sweep that crosses a boundary in both directions.
3. **Install the encode half only, with the wire disabled.** Patches 4.1, 4.2, 4.8, plus the eviction postfixes of 4.10. No `Write`, no `Read`, no flag. Because `GetPosition` is patched on both ends of `GetChangeMask` the offset cancels, so on a single machine the game should behave identically to vanilla while the tables are populated. Test: log the chunk assignment for the puck as it travels the length of a scaled rink and confirm the index steps at the expected thresholds with no thrash at the boundary.
4. **Add the capability announce and the server-side gate.** Extend `ClientVersionCheck` with a chunk-capability bit that is set only after step 5's patches all resolve, plus the `sendStates` wipe on the unsupported-to-supported transition from 4.11. Test: join a server and confirm from logs that the client is classified unsupported first, becomes supported within a second, and that the wipe fires exactly once and produces a burst of reliable full sends.
5. **Turn on the wire.** Patches 4.4, 4.5, 4.6, 4.7, 4.9, and the forced-bit postfix 4.3. Test: modded client against modded server on a scaled rink, which is the first end-to-end test.
6. **Add the unsupported-client re-encode path** inside 4.4 step 2, then verify a deliberately-downlevel client sees vanilla saturation rather than a corrupted stream.
7. **Add the MTU guard and the operator log line** reporting effective ranges, chunk sizes and per-tick payload high-water mark on both ends.

## 9. Kill switch

Three levels, from least to most drastic.

1. **Config flag.** A new `EnableChunkedPositions` boolean on `CompAdjust`, default **false** for the first release. When false, `Capture`'s prefix forces every index to zero, so the encode and decode arithmetic become no-ops, the Write prefix never sets bit 0x2000, and every record is byte-identical to vanilla. This is a live toggle and is safe to flip mid-session because forcing all indices to zero is itself a chunk change on every object, which stamps `forceUntilTick` and forces bits 1|2|4 for a second, so every client receives a corrective full-position record with the flag clear and commits chunk zero on all three axes. Self-healing, no corruption window.
2. **The existing arena gate.** If `GoalNetTweaks.TryGetEffectiveArenaScale` returns false, chunking is disarmed by the same path. That covers a client that never received the sync, which is the vanilla-server case.
3. **Full unpatch.** `ChunkPositionPatch.Disable()` on its own Harmony id. One caveat that must be honoured: **do not unpatch the `Read` postfix while still connected.** Removing it while a server is still appending bytes shreds every array element for the rest of the session. Have `Disable()` skip the `Read` unpatch when `NetworkManager.Singleton.IsConnectedClient`, leaving it resident and inert, since it costs one mask bit test per record when no flag is set. Everything else unpatches freely.

If the design has to be abandoned entirely after live testing, the ordered fallbacks are: first, cap `ArenaScaleX` and `ArenaScaleZ` in config validation so no object can leave the vanilla box, which is zero risk and zero code beyond a clamp; second, enable `SyncRangePatch` and accept 6.1 mm on both axes, which is 8x worse than vanilla on X and 4x on Z but is 20 lines and cannot corrupt a stream.

## 10. What only running the game can settle

1. **Whether the appended bytes actually arrive.** The `FastBufferWriter` by-value cursor sharing is verified structurally (one `WriterHandle*` field) but the capacity bookkeeping inside `TryBeginWrite` through a copied struct is an assumption. Test: modded host plus one modded client, one object parked at chunk (1,0,0). Log the writer position before and after the postfix on the server and the reader position before and after on the client. They must differ by exactly 2 on both ends, and the decoded position must land within a millimetre of the server's true position.
2. **The live LOD and culling `TickRateDivisor` values.** These are serialized MonoBehaviour fields on `SynchronizedObjectManager` and no static analysis can read them. They do not threaten correctness in this design the way they threaten the residue design, but they set how long a lost axis stays stale. Test: log the selected `TickRateDivisor` per object per band while skating from the near boards to the far end of a maximally scaled rink.
3. **The real per-tick object count and payload high-water mark.** This decides whether the throw in `__endSendRpc` is reachable. Test: full lobby, maximum arena scale, every player skating plus the puck in flight, with a log line recording payload bytes per player per tick. Compare the peak against 1272 and confirm the margin.
4. **The join-window fix actually fires before anything sleeps.** Test: park a puck stationary in a far corner of a scaled rink so it is guaranteed asleep, then have a fresh client join. Before the fix that puck renders pinned at the wall forever. After the fix it must snap to its true position within roughly a second of the client's announce. Run it ten times, since the window depends on RTT.
5. **Boundary thrash under real physics.** Test: rest the puck deliberately within a few centimetres of a chunk boundary and watch for a per-tick position resend. The bandwidth counter must not show a per-tick delta for a stationary object. If it does, widen the hysteresis deadband.
6. **The unsupported-client path really is vanilla-shaped.** Test: run a client with the chunk patches deliberately disabled against a chunking server. It must see objects pinned at ±25 and ±50 with a clean stream and no `OverflowException` in its log, not garbage and not a frozen world.
7. **The host actually sees correct physics.** My IL says the host is excluded from the sync path entirely, so its view comes from the real Rigidbodies. Test: run a listen host on a maximally scaled rink and confirm the host's own view of a far object matches the server's simulated position, and that remote clients agree with the host. If the host shows objects pinned at the vanilla wall, my exclusion finding is wrong and the whole plan needs revisiting before anything else.
8. **Which axis is actually the long one.** Resolve the contradiction between `ArenaTweaks.cs:1203-1204` and the wire ranges. Test: log the puck's world position at both blue lines and both side boards on an unscaled rink and read off the true half-extents.