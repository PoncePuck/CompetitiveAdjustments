using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace DashFallMod {
    [HarmonyPatch(typeof(StandardGameMode<StandardGameModeConfig>), "OnGoalScored")]
    public static class GoalScoredSpeedPatch {
        private static readonly FieldInfo _speedField = AccessTools.Field(typeof(Puck), "Speed");
        private static readonly MethodInfo _shotSpeedGetter = AccessTools.PropertyGetter(typeof(Puck), "ShotSpeed");
        private static readonly MethodInfo _scale = AccessTools.Method(typeof(GoalScoredSpeedPatch), nameof(Scale));

        // Called from the patched IL, so the live config value is used.
        public static float Scale(float v) {
            var ct = CompetitiveAdjustments.ConfigManager.CompTweaksEffective;
            return ct == null ? v : v * ct.PuckChatSpeedMult;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
            LocalBuilder tmp = null;

            foreach (var ci in instructions) {
                // puck.Speed.ToString(...): stack holds the ADDRESS of the field.
                if (ci.opcode == OpCodes.Ldflda && Equals(ci.operand, _speedField)) {
                    tmp ??= il.DeclareLocal(typeof(float));

                    // Copy keeps any labels/blocks that were on the original instruction.
                    var load = new CodeInstruction(ci) { opcode = OpCodes.Ldfld };
                    yield return load;                                      // [float]
                    yield return new CodeInstruction(OpCodes.Call, _scale); // [float scaled]
                    yield return new CodeInstruction(OpCodes.Stloc, tmp);   // []
                    yield return new CodeInstruction(OpCodes.Ldloca, tmp);  // [float&] (what ToString expects)
                    continue;
                }

                yield return ci;

                // Plain value loads: field read by value, or the ShotSpeed getter.
                bool isSpeedValueLoad = ci.opcode == OpCodes.Ldfld && Equals(ci.operand, _speedField);
                bool isShotSpeedCall = (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt)
                                       && Equals(ci.operand, _shotSpeedGetter);

                if (isSpeedValueLoad || isShotSpeedCall)
                    yield return new CodeInstruction(OpCodes.Call, _scale); // [float] -> [float scaled]
            }
        }
    }
}
