using Unity.Netcode;

namespace CompetitivePuckTweaks.src
{
    public struct ConfigSyncPackage : INetworkSerializable
    {
        public float PuckScale;
        public float PuckScaleX;
        public float PuckScaleY;
        public float PuckScaleZ;
        public float LegPadOffset;
        public uint BoolFlags;
        public float HighStickingActivateAngle;
        public float HighStickingMaxAngle;

        // Exact serialized size of the field run below, in bytes.  This payload is
        // positional with no field names, so a build that adds or removes a field
        // does not fail to parse against an older peer, it silently reads the wrong
        // value out of the wrong slot (removing TorsoScaleX/Y/Z once shifted
        // HighSticking angles onto the old torso floats: 1.0 instead of -20).
        // A size check catches that skew before a single field is assigned.
        //
        // KEEP IN SYNC with NetworkSerialize: 7 floats + 1 uint = 8 * 4.
        public const int WireSizeBytes = 8 * 4;

        public ConfigSyncPackage(CompetitiveAdjustments.CompTweaksConfig c, CompetitiveAdjustments.CompAdjustConfig df = null)
        {
            PuckScale = c.PuckScale;
            PuckScaleX = c.PuckScaleX;
            PuckScaleY = c.PuckScaleY;
            PuckScaleZ = c.PuckScaleZ;
            LegPadOffset = c.ButterflyPadOffset;
            BoolFlags = PackBools(c, df);
            HighStickingActivateAngle = df?.HighStickingActivateAngle ?? -20f;
            HighStickingMaxAngle     = df?.HighStickingMaxAngle     ?? -80f;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref PuckScale);
            serializer.SerializeValue(ref PuckScaleX);
            serializer.SerializeValue(ref PuckScaleY);
            serializer.SerializeValue(ref PuckScaleZ);
            serializer.SerializeValue(ref LegPadOffset);
            serializer.SerializeValue(ref BoolFlags);
            serializer.SerializeValue(ref HighStickingActivateAngle);
            serializer.SerializeValue(ref HighStickingMaxAngle);
        }

        public static uint PackBools(CompetitiveAdjustments.CompTweaksConfig c, CompetitiveAdjustments.CompAdjustConfig df = null)
        {
            uint b = 0;
            if (c.ThinSkaterBodies)            b |= 1u;
            if (c.EnableSmallerModels)         b |= 1u << 1;
            if (c.EnableGoalieMicrodash)       b |= 1u << 2;
            if (c.RandomPuckDrop)              b |= 1u << 3;
            if (c.EnablePuckThroughBodies)     b |= 1u << 4;
            if (c.EnablePuckThroughGroin)      b |= 1u << 5;
            if (c.PuckDragSpeedDependence)     b |= 1u << 6;
            if (c.PuckHeightDependentDrag)     b |= 1u << 7;
            if (c.DisableStickCollision)       b |= 1u << 8;
            if (c.DisableShaftCollision)       b |= 1u << 9;
            if (c.EnableMidStickCollider)      b |= 1u << 10;
            if (c.AlterStickPositionerOutput)  b |= 1u << 11;
            if (c.EnableStickSpeedDecay)       b |= 1u << 12;
            if (c.EnableSoftBoards)            b |= 1u << 13;
            if (c.EnableJohnBoardBounceTweak)  b |= 1u << 14;
            if (c.BananaMode)                  b |= 1u << 15;
            if (df?.FreeBladeEnabled           == true) b |= 1u << 18;
            if (df?.HighStickingEnabled        == true) b |= 1u << 19;
            if (df?.BallMode                  == true) b |= 1u << 20;
            if (df?.StickBodyCollision        == true) b |= 1u << 21;
            // A spare bit in the existing uint rather than a new field, which is what makes
            // this safe to add: WireSizeBytes does not move, so no peer's size check trips
            // and no field shifts onto another field's slot. A server too old to set it
            // sends 0, and 0 is the right answer for a server that does not have the
            // feature. That matters more here than for the flags above: the field also
            // travels in the PPKB/ConfigFull JSON, but JsonUtility.FromJsonOverwrite leaves
            // absent fields alone, so a client on an older server would otherwise have kept
            // its own default of ON and rate-limited its blade while nobody else was.
            if (df?.StickSpinFatigueEnabled   == true) b |= 1u << 22;
            // Wrapped ("chunked") positions. Must be synced: the client only honours the
            // marker bit when it knows the server is sending it, and a one-sided belief
            // here displaces every object by a whole period.
            // The EFFECTIVE state, not the raw config value. If the operator asked for
            // wrapping but this build declined to arm (self-test failed, hooks missing),
            // advertising the raw flag would have clients drop to the vanilla range while
            // the server kept widening, putting every position out by the range ratio.
            // Pure STATUS now, not an operator setting: "this server is wrapping positions,
            // so hold your wire range at vanilla". The client cannot work this out for
            // itself, and if the two ends disagree every position is out by the ratio
            // between the two ranges, so it has to travel.
            if (DashFallMod.Net.WrapSync.WrappingGovernsRange) b |= 1u << 23;
            return b;
        }

        public static void UnpackBools(uint b, CompetitiveAdjustments.CompTweaksConfig c)
        {
            c.ThinSkaterBodies            = (b & 1u) != 0;
            c.EnableSmallerModels         = (b & (1u << 1)) != 0;
            c.EnableGoalieMicrodash       = (b & (1u << 2)) != 0;
            c.RandomPuckDrop              = (b & (1u << 3)) != 0;
            c.EnablePuckThroughBodies     = (b & (1u << 4)) != 0;
            c.EnablePuckThroughGroin      = (b & (1u << 5)) != 0;
            c.PuckDragSpeedDependence     = (b & (1u << 6)) != 0;
            c.PuckHeightDependentDrag     = (b & (1u << 7)) != 0;
            c.DisableStickCollision       = (b & (1u << 8)) != 0;
            c.DisableShaftCollision       = (b & (1u << 9)) != 0;
            c.EnableMidStickCollider      = (b & (1u << 10)) != 0;
            c.AlterStickPositionerOutput  = (b & (1u << 11)) != 0;
            c.EnableStickSpeedDecay       = (b & (1u << 12)) != 0;
            c.EnableSoftBoards            = (b & (1u << 13)) != 0;
            c.EnableJohnBoardBounceTweak  = (b & (1u << 14)) != 0;
            c.BananaMode                  = (b & (1u << 15)) != 0;
        }

        public static void UnpackDashfall(ConfigSyncPackage pkg, CompetitiveAdjustments.CompAdjustConfig df)
        {
            if (df == null) return;
            df.FreeBladeEnabled             = (pkg.BoolFlags & (1u << 18)) != 0;
            df.HighStickingEnabled          = (pkg.BoolFlags & (1u << 19)) != 0;
            df.HighStickingActivateAngle    = pkg.HighStickingActivateAngle;
            df.HighStickingMaxAngle         = pkg.HighStickingMaxAngle;
            df.BallMode                     = (pkg.BoolFlags & (1u << 20)) != 0;
            df.StickBodyCollision           = (pkg.BoolFlags & (1u << 21)) != 0;
            // Server wrapping status. Deliberately NOT stored on the config object: it is
            // replicated state, and a config field would also travel in the ConfigFull JSON
            // carrying the server's stale on-disk value, clobbering this one.
            DashFallMod.Net.WrapSync.SetServerWrapping((pkg.BoolFlags & (1u << 23)) != 0);
            df.StickSpinFatigueEnabled      = (pkg.BoolFlags & (1u << 22)) != 0;
        }
    }
}
