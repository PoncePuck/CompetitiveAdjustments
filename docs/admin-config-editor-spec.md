# Admin Config Editor: in-game live editing of the full server config

Status: specced 2026-06-05, implementation pending. This document is the working brief for the implementation session. Paste it (or open it) at the start of that session.

> Implementation note (auth simplified after this spec was written): the shipped code does NOT use the salted-SHA256 hash, the `SetPasswordPlaintext` field, or the `AdminSteamIds` allowlist described below. Admin identity is the game's own flag (`player.AdminLevel.Value > 0`, like the other mods), and the password is a single random `Admin.EditorPassword` auto-generated on first config write, stored in plaintext server-side (never sent on the wire), and shown to the host on the SERVER tab. The rest of this brief (sync pipeline, editor UI, wire stripping) still describes the implementation.

## 1. Goal

Make the SERVER tab of the in-game mod panel an admin-gated editor for the entire server config. An authenticated admin (on any connected client) can change every config field, press one SAVE & APPLY button, and the server writes the file to disk, reloads, applies the changes live, and re-syncs the new config to every connected client. Non-admins see the same editor greyed out behind a lock.

## 2. Locked decisions

These were settled in a requirements interview. Do not relitigate them; build to them.

| Topic | Decision |
| --- | --- |
| Auth | Password plus Steam ID allowlist. Allowlisted Steam IDs unlock automatically; everyone else can type a password to unlock. |
| Edit reach | Any authenticated client edits remotely. Edits travel to the server, which validates, applies, saves, and re-syncs. |
| Editable scope | Everything: all booleans and all numeric tuning across Dashfall, CompAdjust, CompTweaks, plus the three master EnableX flags. |
| Apply timing | Live and immediate on save. |
| Client config sync | Broadcast the full ServerConfig to clients as JSON so a remote admin sees true live values. |
| Apply trigger | Batched. The admin tweaks fields locally, then one SAVE & APPLY button sends the whole config in a single message. No per-field network spam. |
| Credential storage | Inside CompetitiveAdjustments.json. |
| Non-admin view | Full editor shown but greyed out behind a lock icon. |

## 2.1 Validation pass against the code (2026-06-05)

This spec was re-checked against the actual source after the first draft. Corrections that materially change the build:

1. The single-named-message assumption was wrong and is corrected in section 7. UnityTransport caps a single reliable message (default Max Payload Size is around 6 KB), and the full config is roughly that size. The [src/Net/](src/Net/) "chunk" system is NOT a generic large-payload transport and cannot be reused for this; it chunks world space so positions fit a 16-bit wire range (see the +/-50 m comments in [ChunkSyncServer.cs:38-43](src/Net/ChunkSyncServer.cs#L38)). For the config JSON, send compact JSON and add a small manual string splitter if it exceeds the transport cap. Do not point this at the position chunk system.
2. The client must not run the server apply hooks. `ConfigManager.ReloadConfig` calls `SyncFeatureStates` and `NotifyConfigReloaded` ([ServerConfig.cs:524-525](src/ServerConfig.cs#L524)), which set client-side feature gates (`GoalieDashExtend.Enabled`, `Stances.Enabled`) from the config. The new client `LoadFromJson` path must populate the section data for display and editing only, guarded so those server-authoritative hooks do not fire on a client. See the revised section 5.2.
3. The host (listen server) save must apply in-process, not via a self-addressed named message. `SendNamedMessageToAll` does not self-deliver to the host (noted in [Stances.cs:153](src/Stances.cs#L153) and [GoalieDashExtend.cs:210](src/GoalieDashExtend.cs#L210)), and round-tripping a message to your own client id is unreliable. When `NetworkManager.Singleton.IsServer` is true, the SAVE button calls the server apply path directly. See revised sections 8.2 and 10.
4. There is no precedent in the codebase for sending a raw string over a named message; all current senders use fixed structs or small fixed buffers. The full-config sender needs a writer sized to the payload using the three-argument `FastBufferWriter(initial, Allocator.Temp, max)` form (precedent at [ChunkSyncServer.cs:269](src/Net/ChunkSyncServer.cs#L269)), and the reader must read the string with the matching `ReadValueSafe`.
5. Float reflection rows can reuse `MakeFloatRow` directly by passing very wide bounds, no new widget needed. See revised section 10.

## 3. The most important finding: most of the server pipeline already exists

A large part of this feature is already implemented for the `/reload` chat command. Reuse it instead of rebuilding.

1. Admin identity is already solved. [SmallPatches.cs:388](src/SmallPatches.cs#L388) `IsAdmin(ulong clientId)` returns true for the listen-server host and for any player whose Steam ID is in Puck's built-in admin list via `ServerManager.Instance.AdminManager.IsSteamIdAdmin(player.SteamId.Value.ToString())`. This already provides the Steam allowlist half of our auth for free.
2. A player's Steam ID is reachable as `player.SteamId.Value.ToString()`, and a client id maps to a player via `PlayerManager.Instance.GetPlayerByClientId(clientId)` (see [SmallPatches.cs:395-401](src/SmallPatches.cs#L395-L401)).
3. There is already an "open to everyone" escape hatch: `CompTweaksConfig.OpenConfigChanges` ([ServerConfig.cs:245](src/ServerConfig.cs#L245)) is checked at [SmallPatches.cs:334](src/SmallPatches.cs#L334) to bypass the admin gate for config commands.
4. The exact apply-and-broadcast sequence we need already exists in `ReloadServerConfig` ([SmallPatches.cs:413-446](src/SmallPatches.cs#L413-L446)):
   1. `ConfigManager.EnsureConfig()` then `ConfigManager.ReloadConfig()`.
   2. `PluginCore.ApplyLiveConfigFull()` for live physics and feature apply.
   3. `GoalNetTweaks.RefreshAll()`.
   4. `ServerBridge.BroadcastFeaturesToAllClients()` and `ServerBridge.BroadcastGoalTweaksToAllClients()`.
   5. Per-player `PluginCore.ManualSync(clientId)`.
   6. `SendSystemMessage` feedback to the requester.
5. Live apply already handles the mid-game risky fields. `PluginCore.ApplyLiveConfig()` ([Tweaks.PluginCore.cs:560-583](src/Tweaks.PluginCore.cs#L560)) sets `Time.fixedDeltaTime`, `Physics.defaultSolverIterations`, layer collisions, rescales pucks, refreshes ball mode, free blade, and torso meshes. So our "live immediately" requirement is already met by this path; we do not need a separate apply system.
6. The command transport pattern is a Harmony prefix on chat: `ChatManagerCommandPatch` ([SmallPatches.cs:309-386](src/SmallPatches.cs#L309)) intercepts `ChatManager.Client_SendChatMessageRpc`, reads `rpcParams.Receive.SenderClientId`, and dispatches. This is one option for the client-to-server channel, but see section 7 for why a named message is cleaner here.

Net effect: the server-side SAVE handler is essentially `ReloadServerConfig` with the source changed from "read disk" to "accept the client-pushed config, write it to disk, then run the same tail." Refactor the tail of `ReloadServerConfig` into a shared helper and call it from both paths.

## 4. Current client sync is three partial channels, not the whole config

Today a client never receives the full config. It receives three narrow slices, and the SERVER tab reads them plus the local `ConfigManager.Config` to render its read-only display ([DashFall.UI.cs:402-503](src/DashFall.UI.cs#L402)):

1. `PPKB/Features`: a `ushort` of role feature flags. Packed in `ServerFeatures.ToUShort()` ([DashFall.ServerBridge.cs:29](src/DashFall.ServerBridge.cs#L29)), received at [DashFall.ServerBridge.cs:235](src/DashFall.ServerBridge.cs#L235).
2. `PPKB/GoalTweaks`: goal and arena floats. Sent at [DashFall.ServerBridge.cs:402](src/DashFall.ServerBridge.cs#L402), received at [DashFall.ServerBridge.cs:251](src/DashFall.ServerBridge.cs#L251).
3. `CMM_SYNC_CONFIG`: the `ConfigSyncPackage` subset (puck scale, leg pad, ~22 bool flags, torso, high-stick). Built in [Tweaks.ConfigSyncPackage.cs](src/Tweaks.ConfigSyncPackage.cs), sent by `PluginCore.ManualSync` ([Tweaks.PluginCore.cs:458-473](src/Tweaks.PluginCore.cs#L458)).

For a remote admin to see and edit real current values for every field, we must add a fourth channel that carries the full ServerConfig. That is the new work, alongside the editor UI.

## 5. Data model changes

### 5.1 Add an Admin section to ServerConfig

In [ServerConfig.cs](src/ServerConfig.cs), add a new serializable type and a top-level field on `ServerConfig`:

```csharp
[Serializable]
public class AdminAuthConfig
{
    // Empty password means the password path is disabled (allowlist only).
    public string PasswordHash = "";      // SHA-256 hex of the password, see 9.2
    public string PasswordSalt = "";      // random per-install salt
    public string[] AdminSteamIds = new string[0]; // additive to Puck's AdminManager
}
```

Add `public AdminAuthConfig Admin = new AdminAuthConfig();` to `ServerConfig`.

Important serialization detail: config is not written with a single `JsonUtility.ToJson(ServerConfig)`. `WriteConfig` ([ServerConfig.cs:431-458](src/ServerConfig.cs#L431)) manually stitches `ConfigVersion`, the three enables, and the three sections. `ReloadConfig` ([ServerConfig.cs:465-533](src/ServerConfig.cs#L465)) reads them back with `ExtractSection` and `JsonUtility.FromJsonOverwrite`. To persist Admin you must:

1. Serialize the Admin block in `WriteConfig` (stitch it like the other sections).
2. Extract and load it in `ReloadConfig`.
3. Bump `ServerConfig.CURRENT_VERSION` (currently 12) and add Admin to the `NeedsUpgrade` checks so existing configs gain the section on first load.

This manual stitching is an advantage for security: because we control exactly which blocks are written, we can produce a credential-free serialization for the client broadcast by simply omitting the Admin block (see section 9.1).

### 5.2 Client-side full-config mirror

The client needs somewhere to hold the received full config so the editor can render and edit it. Reuse the existing `CompetitiveAdjustments.ConfigManager.Config` object on the client (it already exists in process and the UI already reads it at [DashFall.UI.cs:438-501](src/DashFall.UI.cs#L438)). On receiving the full-config message, deserialize into the client's `ConfigManager.Config` via the same per-section `JsonUtility.FromJsonOverwrite` calls `ReloadConfig` uses, through a new `ConfigManager.LoadFromJson(string)` helper so the network handler does not duplicate parsing logic. Track a `HasReceivedFullConfig` flag and an `OnFullConfigReceived` event mirroring `ServerBridge.HasReceivedFeatures` / `OnFeaturesReceived`.

Critical difference from `ReloadConfig`: `LoadFromJson` on a client must NOT run the server-authoritative apply hooks. `ReloadConfig` ends with `SyncFeatureStates(cfg)` and `NotifyConfigReloaded()` ([ServerConfig.cs:524-525](src/ServerConfig.cs#L524)), which set the static client gates `GoalieDashExtend.Enabled` and `Stances.Enabled` from the config ([ServerConfig.cs:586-588](src/ServerConfig.cs#L586)) and re-run `ApplySubModEnables`. On a client those gates are driven by `ServerBridge.ReceivedFeatures`, not by this display mirror, so running them here would let the synced config silently repurpose the client's feature gating. `LoadFromJson` should only overwrite the three section objects (and the enables) and fire `OnFullConfigReceived`. It must skip `SyncFeatureStates` and `NotifyConfigReloaded` unless `NetworkManager.Singleton.IsServer` is true.

Side benefit: today the SERVER tab's "ALL ... BOOLS" sections ([DashFall.UI.cs:486-502](src/DashFall.UI.cs#L486)) read the client's local `ConfigManager.Config`, which for fields outside the `ConfigSyncPackage` subset is still at type defaults, so they currently display defaults rather than real server values. Populating the mirror from `PPKB/ConfigFull` fixes that display bug as a free side effect.

## 6. Architecture overview

```
[Admin client UI]                         [Server]
 SERVER tab editor                          ChatManagerCommandPatch / ServerBridge
   |  edits local copy of Config              |
   |  press UNLOCK -> PPKB/AdminAuth --------> validate (Steam allowlist OR password)
   |     <----------- PPKB/AdminAuthResult --- grant/deny, track authed clientId
   |  press SAVE & APPLY -> PPKB/AdminConfigSet (full JSON) -->
   |                                          re-check authed, parse, overwrite Config,
   |                                          SaveConfig(), ApplyAndBroadcast()
   |                                              |  ApplyLiveConfigFull
   |                                              |  GoalNetTweaks.RefreshAll
   |                                              |  Broadcast Features + GoalTweaks
   |                                              |  per-player ManualSync
   |  <----------- PPKB/ConfigFull (creds-stripped JSON) broadcast to ALL clients
 all clients refresh their Config mirror; editor shows new live values
```

## 7. Network protocol

Use `CustomMessagingManager` named messages, matching the existing `PPKB/*` and `CMM_SYNC_CONFIG` conventions. Register the new handlers in `ServerBridge.Runner.Update` alongside the others ([DashFall.ServerBridge.cs:196-200](src/DashFall.ServerBridge.cs#L196)) and unregister them in `TryUnregister` and `Unhook`.

Reason to prefer a named message over extending the chat-command path for the actual config payload: the config JSON is multi-kilobyte and chat commands are size-limited and user-visible. Auth and the config push should be binary named messages. The chat `/reload` and `/forcesync` commands can remain as they are.

New messages:

1. `PPKB/AdminAuth` (client to server): one string, the plaintext password the user typed. Empty string means "I am allowlisted, please grant me" (server still verifies via Steam allowlist or Puck AdminManager).
2. `PPKB/AdminAuthResult` (server to client): one bool granted, plus an optional reason string for the UI.
3. `PPKB/AdminConfigSet` (client to server): one string, the full edited config as JSON (the three sections plus the three enables, never the Admin block). Server ignores any Admin data that arrives here.
4. `PPKB/ConfigFull` (server to client): the credential-free full config JSON. Sent to a single client in `SendInitialStateToClient` ([DashFall.ServerBridge.cs:391-400](src/DashFall.ServerBridge.cs#L391)) and to all clients at the end of `ApplyAndBroadcast`. May be one message or several fragments, see the size handling below.

Writer sizing: there is no existing code that sends a raw string over a named message, so do not copy the fixed-size `FastBufferWriter(N, Allocator.Temp)` pattern used for the small struct messages. Size the writer to the payload using the three-argument form with headroom, as the bulk-snapshot path does at [ChunkSyncServer.cs:269](src/Net/ChunkSyncServer.cs#L269): `int n = Encoding.UTF8.GetByteCount(json); var w = new FastBufferWriter(n + 8, Allocator.Temp, n + 8);` then `w.WriteValueSafe(json)`. The receiver reads it back with `reader.ReadValueSafe(out string json)`.

Message-size handling (corrected): do NOT assume a single named message will fragment for you, and do NOT reuse the [src/Net/](src/Net/) chunk system as a fallback. That system chunks world space to keep positions inside a 16-bit wire range; it is not a generic byte transport (see [ChunkSyncServer.cs:11-43](src/Net/ChunkSyncServer.cs#L11)). UnityTransport enforces a per-message cap (its Max Payload Size, default about 6 KB) and a single config is right around that. Plan:

1. First measure. Serialize the three sections plus enables as compact (non-indented) JSON and log the byte count. Roughly 250 fields lands near 5 to 8 KB pretty-printed, less when compact.
2. If it fits comfortably under the transport cap, send it as one `PPKB/ConfigFull` string message.
3. If it does not fit, send it as a tiny custom multi-part sequence over the same named message: a header byte for part index and part count followed by a substring, reassembled on the client by concatenation keyed on a transfer id. This is a few dozen lines and avoids touching the position chunk system. Keep parts under about 4 KB each.

Either way, send compact JSON on the wire and only pretty-print for the on-disk file.

## 8. Server-side flow

### 8.1 Auth tracking

Add a server-side `HashSet<ulong> _authedClients` (in `ServerBridge` or a small new `AdminAuthServer` static). On `PPKB/AdminAuth`:

1. Resolve the player and Steam ID via `PlayerManager.Instance.GetPlayerByClientId(senderId)`.
2. Grant if any of: the existing `IsAdmin(senderId)` logic returns true (host or Puck admin), the Steam ID is in `Config.Admin.AdminSteamIds`, `OpenConfigChanges` is true, or the supplied password verifies against `Config.Admin.PasswordHash` and salt.
3. On grant, add `senderId` to `_authedClients` and reply `PPKB/AdminAuthResult(true)`. On deny, reply false with a reason.
4. Remove the client id from `_authedClients` in the existing `OnClientLeft` disconnect handler ([DashFall.ServerBridge.cs:227-231](src/DashFall.ServerBridge.cs#L227)).

Move or share `IsAdmin` so both the chat-command path and the new auth path use one implementation. It is currently private in `ChatManagerCommandPatch`.

### 8.2 Apply handler

The handler runs only on the server (guard `NetworkManager.Singleton.IsServer` at the top, like `OnHello` at [DashFall.ServerBridge.cs:501-503](src/DashFall.ServerBridge.cs#L501)). On `PPKB/AdminConfigSet`:

1. Reject if `senderId` is not in `_authedClients` (defense in depth, do not trust the client UI).
2. Parse the JSON into the live `ConfigManager.Config`, overwriting only the three sections and the three enables (reuse `LoadFromJson`). Never extract or read an Admin block from an inbound payload, even if a malicious client includes one.
3. `ConfigManager.SaveConfig()` to persist to disk.
4. Call the shared apply-and-broadcast helper (see 8.3).
5. Send a result confirmation to the requester (`PPKB/AdminAuthResult`-style or a chat system message).

Host (listen server) shortcut: the host is its own server and `SendNamedMessageToAll` does not self-deliver, so the host UI must not push its own edits over the network. When the SAVE button runs on a client where `NetworkManager.Singleton.IsServer` is true, skip `PPKB/AdminConfigSet` entirely and call the same in-process routine this handler uses (overwrite Config, SaveConfig, ApplyAndBroadcast). Factor steps 2 through 5 into one server-side method so both the message handler and the host shortcut call it.

### 8.3 Refactor ReloadServerConfig into a reusable helper

Extract the tail of `ReloadServerConfig` ([SmallPatches.cs:420-440](src/SmallPatches.cs#L420)) into a shared method, for example `ConfigApplyService.ApplyAndBroadcast()`, that runs `ApplyLiveConfigFull`, `GoalNetTweaks.RefreshAll`, the two ServerBridge broadcasts, per-player `ManualSync`, and the new full-config broadcast. Then:

1. `/reload` becomes: ReloadConfig from disk, then `ApplyAndBroadcast`.
2. The admin editor becomes: overwrite Config from the pushed JSON, SaveConfig, then `ApplyAndBroadcast`.

Add the new `PPKB/ConfigFull` broadcast as the final step inside `ApplyAndBroadcast` so both `/reload` and editor saves keep clients fully in sync.

## 9. Security requirements

### 9.1 Critical: never broadcast the Admin block

This is the headline risk created by storing credentials in the same JSON we broadcast. The serialization used for `PPKB/ConfigFull` and `PPKB/AdminConfigSet` must contain only ConfigVersion, the three enables, and the Dashfall, CompAdjust, CompTweaks sections. It must never contain the Admin block. Because `WriteConfig` already stitches blocks by hand, implement a `SerializeForWire()` that produces the same content minus Admin, and use it for the wire. Add an explicit assertion or unit-style check that the wire payload does not contain `"Admin"`, `"PasswordHash"`, or `"AdminSteamIds"`.

### 9.2 Password handling

1. Store a salted SHA-256 hash, not the plaintext, in `Config.Admin.PasswordHash` plus `PasswordSalt`. The decision was "store in the main JSON"; hashing is a cheap upgrade that avoids a plaintext password sitting on disk. If the user insists on literal plaintext, gate it behind a clearly named field, but default to hashed.
2. The password still travels in plaintext inside `PPKB/AdminAuth` over the Netcode transport, which is not guaranteed encrypted. Document this. It is acceptable for a game mod but should be stated. Never log the password. Never echo it back in `PPKB/AdminAuthResult`.
3. Verify by hashing the supplied password with the stored salt and comparing to the stored hash.

### 9.3 Server is the only authority

The client UI lock is cosmetic. Every `PPKB/AdminConfigSet` is re-validated against `_authedClients` server-side. A modified client that sends the message without auth must be rejected.

## 10. UI changes (SERVER tab)

All UI lives in the `DashFallClientRunner` partial class in [DashFall.UI.cs](src/DashFall.UI.cs). Reuse the existing editable row builders from the SETTINGS tab: `MakeToggleRow` ([DashFall.UI.cs:676](src/DashFall.UI.cs#L676)), `MakeFloatRow` ([DashFall.UI.cs:724](src/DashFall.UI.cs#L724)), `MakeSliderRow` ([DashFall.UI.cs:793](src/DashFall.UI.cs#L793)). The read-only `MakeServerConfigRow` ([DashFall.UI.cs:1042](src/DashFall.UI.cs#L1042)) is replaced by editable rows for admins.

1. Replace `BuildServerConfigUI` ([DashFall.UI.cs:402](src/DashFall.UI.cs#L402)) so it builds editable rows bound to the client's `ConfigManager.Config` mirror. Edits update the local copy only; they are not sent until SAVE & APPLY.
2. Generate rows by reflection so all roughly 250 fields appear automatically and new fields need no UI changes. The file already enumerates bool fields via `EnumerateBoolFields` ([DashFall.UI.cs:1069](src/DashFall.UI.cs#L1069)) and humanizes names via `HumanizeBoolFieldName` ([DashFall.UI.cs:1093](src/DashFall.UI.cs#L1093)). Extend this to also enumerate `float` and `int` fields and emit `MakeFloatRow` for them. Group by the three sections with `AddSectionHeader` and `AddSubHeader`.
3. Float fields need min and max for `MakeFloatRow`. Reflection cannot infer sane ranges, and no new widget is required: `MakeFloatRow` already parses free text and only clamps on focus-out ([DashFall.UI.cs:775-787](src/DashFall.UI.cs#L775)), so pass very wide bounds (for example `float.NegativeInfinity` and `float.PositiveInfinity`, or a large finite range) and the clamp becomes a no-op, giving an effectively free-entry numeric field. Optionally keep a small dictionary of curated ranges for the handful of fields where a real slider helps. For `int` fields (`SolverIterations`, `StickSpeedDecaySpan`), reuse `MakeFloatRow` and round the float result to int in the setter.
4. Lock overlay for non-admins: render the full editor but disabled. Put a semi-transparent overlay and a lock icon plus an UNLOCK button over the SERVER tab content. Clicking UNLOCK opens a password prompt (a `TextField` with `isPasswordField = true` and a SUBMIT button). On submit, send `PPKB/AdminAuth`. On `PPKB/AdminAuthResult(true)`, remove the overlay and enable the controls; on false, show the reason. Auto-unlock for trusted users: if `NetworkManager.Singleton.IsServer` is true (the host), unlock immediately with no message at all. Otherwise, on opening the panel send `PPKB/AdminAuth` with an empty password and unlock if granted (covers Steam allowlist and `OpenConfigChanges`).
5. Add a SAVE & APPLY button at the bottom of the SERVER tab (model it on the existing bottom button row built around [DashFall.UI.cs:208-230](src/DashFall.UI.cs#L208)). On click, serialize the local Config to compact wire JSON. If `NetworkManager.Singleton.IsServer` is true, call the in-process server apply routine directly (see 8.2 host shortcut); otherwise send `PPKB/AdminConfigSet`. Show a transient "Applied" or error status returned by the server. Keep a RESET TO DEFAULTS action for the server config too, which just loads a fresh `ServerConfig()` into the editor (not applied until SAVE).
6. Session model: once unlocked, stay unlocked while the panel and connection persist. Add a small LOCK button to drop back to read-only without disconnecting. Re-auth is required after a reconnect because the server clears `_authedClients` on disconnect.

## 11. Edge cases and risks

1. Listen-server host editing locally: the host is already an admin via `IsAdmin` and is server-authoritative, so the same SAVE path works without network round-trips, but it must still go through the apply-and-broadcast helper so remote clients update.
2. Two admins editing at once: last write wins. The `PPKB/ConfigFull` broadcast after each save will refresh the other admin's editor, which can overwrite their unsaved local edits. Acceptable for v1; optionally warn "config changed by another admin" if a broadcast arrives while the local editor has unsaved changes.
3. Mid-game physics fields (`FixedDeltaTime`, `SolverIterations`): already applied live by `ApplyLiveConfig`. Sanity-check that extreme values entered in the editor cannot brick the session; consider clamping these two specifically.
4. Master enables interaction: the `Effective` accessors ([ServerConfig.cs:346-377](src/ServerConfig.cs#L346)) gate sections. Editing a field while its section is disabled still saves the value but has no live effect until the section is enabled. The editor should show section-enable toggles prominently so this is not confusing.
5. Field type coverage: ServerConfig has bool, float, and int (`SolverIterations`, `StickSpeedDecaySpan`). Make sure the reflection path handles int, not just float.
6. JSON size on the wire: measure the compact payload first (section 7). If it exceeds the UnityTransport per-message cap, use the small multi-part string sender described in section 7. Do not route this through the [src/Net/](src/Net/) position chunk system; it is not a byte transport.
7. Config version upgrade: bumping `ServerConfig.CURRENT_VERSION` makes `NeedsUpgrade` trigger a rewrite on load; make sure the Admin block survives an upgrade rewrite rather than being wiped.

## 12. Implementation task checklist (suggested order)

1. ServerConfig: add `AdminAuthConfig` and `ServerConfig.Admin`; update `WriteConfig`, `ParseConfig`, the `NeedsUpgrade` checks, and bump `ServerConfig.CURRENT_VERSION`. Add `SerializeForWire()` (creds-stripped, compact) and `LoadFromJson(string)` helpers. `LoadFromJson` overwrites only the sections plus enables and must skip `SyncFeatureStates` / `NotifyConfigReloaded` unless `IsServer`.
2. Shared auth: extract `IsAdmin` into a shared, server-side helper and add password verify (salted SHA-256) plus the `AdminSteamIds` allowlist check and the existing `OpenConfigChanges` bypass.
3. Server messages: add `_authedClients` and handlers for `PPKB/AdminAuth` and `PPKB/AdminConfigSet`; register and unregister them in `ServerBridge.Runner`; clear on disconnect.
4. Apply refactor: extract `ApplyAndBroadcast()` from `ReloadServerConfig`; add the `PPKB/ConfigFull` broadcast as its final step; route `/reload` and the editor save through it.
5. Full-config sender plus client receive: measure the compact payload; send `PPKB/ConfigFull` as one string message if it fits the transport cap, otherwise add the small multi-part sender from section 7. Handle it on the client through `LoadFromJson`, set `HasReceivedFullConfig`, fire `OnFullConfigReceived`. Also send it from `SendInitialStateToClient` so clients get it on connect.
6. UI: reflection-driven editable rows in `BuildServerConfigUI` (bool, float, int), section grouping, lock overlay plus password prompt, auto-unlock for host and allowlisted, SAVE & APPLY button with the host in-process shortcut, status feedback, LOCK button, RESET TO DEFAULTS.
7. Security check: assert the wire JSON never contains Admin or credential keys, and that the inbound `PPKB/AdminConfigSet` parser never reads an Admin block.

## 13. Acceptance tests

1. An allowlisted Steam ID (Puck admin or in `AdminSteamIds`) opens the SERVER tab and it is unlocked automatically.
2. A non-allowlisted player must enter the correct password to unlock. A wrong password is rejected and changes nothing.
3. An admin toggles a bool and edits a numeric field, presses SAVE & APPLY. Verify the change is written to CompetitiveAdjustments.json, applied live on the server (observe the gameplay effect), and reflected on a second connected client without a reconnect.
4. Inspect the `PPKB/ConfigFull` and `PPKB/AdminConfigSet` payloads and confirm they contain no password, salt, or Steam IDs.
5. A client that sends `PPKB/AdminConfigSet` without having authed is rejected server-side.
6. After a client disconnects and reconnects, it must re-auth before editing.
7. The existing `/reload` and `/forcesync` chat commands still work and now also push the full config.

## 14. Open questions to confirm with the user before or during build

1. Password storage: ship hashed plus salted (recommended) or literal plaintext as originally stated. Default to hashed unless told otherwise.
2. Float ranges in the reflection editor: free-entry numeric (recommended for completeness) versus curated min and max sliders for known fields.
3. Whether `AdminSteamIds` in our config should be additive to Puck's `AdminManager` list (recommended) or the sole source.
4. Whether non-admins should see real current values in the greyed editor or just defaults (recommended: real values, since `PPKB/ConfigFull` is broadcast to everyone anyway).
