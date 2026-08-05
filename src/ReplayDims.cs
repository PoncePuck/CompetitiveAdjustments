// ReplayDims.cs
// Exposes the server's live competitive rink/goal geometry as raw numbers, for the Replay
// Mod to read and store privately inside the replay. Nothing is sent to players: this is the
// quiet path, the geometry never appears in chat. The Replay Mod reflects CurrentDims() on
// the recording machine, polls it while recording, and writes a dims event when the values
// change.
//
// Read authority follows the machine. On the server this is the live authoritative config;
// on a client it is whatever CA has synced from the server (CompAdjustEffective collapses to
// the vanilla baseline on a client that has not received a sync). A recorder without CA
// loaded finds no method at all and records no dims -- read back as a vanilla rink.
//
// The float[] layout is the cross-mod contract, matched by the Replay Mod's
// CompAdjustDimsCodec. It is APPEND-ONLY: never reorder or remove a slot, only add new ones
// at the end and bump DimsSchemaVersion, so an older reader keeps reading the slots it knows.
// Values are what is ACTUALLY applied -- the two sub-toggles gate the arena and goal blocks
// the same way GoalNetTweaks gates them, so a value left in a disabled section reads as its
// vanilla default rather than as config the server ignores.

namespace CompetitiveAdjustments
{
    public static class ReplayDims
    {
        // Bump when a slot is appended to the layout below. Slot [0] carries this so the
        // reader can branch; existing slots never move.
        public const int DimsSchemaVersion = 1;

        /// <summary>
        /// The rink/goal geometry currently in effect, as a raw float[] for the Replay Mod,
        /// or the vanilla baseline when nothing is adjusted. Reflected into by PuckReplayMod:
        /// keep the type name, method name, and signature stable.
        ///
        /// Layout: [0] schema version, [1..3] arena scale X/Y/Z, [4..6] arena offset X/Y/Z,
        /// [7..9] goal size X/Y/Z, [10] goal thickness, [11] goal back offset.
        /// </summary>
        public static float[] CurrentDims()
        {
            var c = ConfigManager.CompAdjustEffective ?? new CompAdjustConfig();

            bool arena = c.EnableArenaTweaks;
            bool goal = c.EnableGoalNetTweaks;

            // Report what is ACTUALLY applied, not raw config. GoalNetTweaks.RefreshAll maps
            // NaN/Inf to the default (ResolveScale/ResolveOffset) and floors scales at 0.1 /
            // thickness at 0.05 (Mathf.Max) before the value reaches the rink. Mirror that so a
            // below-floor or non-finite config value is recorded as the geometry the match was
            // really played on -- and so a raw NaN never lands in the replay stream. The
            // client-only synced min/max clamp is not reproduced; the server recorder (the
            // authoritative one) is exact.
            return new float[]
            {
                DimsSchemaVersion,
                Scale(arena, c.ArenaScaleX, 0.1f),
                Scale(arena, c.ArenaScaleY, 0.1f),
                Scale(arena, c.ArenaScaleZ, 0.1f),
                Offset(arena, c.ArenaOffsetX),
                Offset(arena, c.ArenaOffsetY),
                Offset(arena, c.ArenaOffsetZ),
                Scale(goal, c.GoalSizeScaleX, 0.1f),
                Scale(goal, c.GoalSizeScaleY, 0.1f),
                Scale(goal, c.GoalSizeScaleZ, 0.1f),
                Scale(goal, c.GoalThicknessScale, 0.05f),
                Offset(goal, c.GoalBackOffset),
            };
        }

        // A disabled block reads as the vanilla default. Otherwise NaN/Inf -> 1 (matches
        // ResolveScale), then floor (matches the Mathf.Max in RefreshAll). NaN must be caught
        // before the floor compare: "NaN < floor" is false, which would pass the NaN through.
        private static float Scale(bool applied, float v, float floor)
        {
            if (!applied) return 1f;
            if (float.IsNaN(v) || float.IsInfinity(v)) return 1f;
            return v < floor ? floor : v;
        }

        // Offsets have no floor; NaN/Inf -> 0 (matches ResolveOffset).
        private static float Offset(bool applied, float v)
        {
            if (!applied) return 0f;
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }
    }
}
