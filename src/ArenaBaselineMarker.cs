// ArenaBaselineMarker.cs - remember an object's vanilla transform ON the object itself.

using UnityEngine;

namespace DashFallMod
{
    /// <summary>
    /// Records what a transform looked like before the arena resize touched it, stored as
    /// a component on the object rather than in a static dictionary.
    ///
    /// Every attempt to hold this in statics has failed the same way. The resize is
    /// "target = vanilla baseline x config", so the baseline has to be the UNTOUCHED value,
    /// and every scheme for deciding when to re-measure it has been wrong:
    ///
    ///   Keyed by instance id: ids are unique only among live objects, and the level root
    ///   is re-resolved by a name search that can land on a different node once a scenery
    ///   mod adds arena-shaped ones. A changed id re-measured an already-scaled rink.
    ///
    ///   Cleared on level spawn: the object is not always gone. Restoring before clearing
    ///   fixed the surviving-object case, but not the next one.
    ///
    ///   The case that broke both: the incoming level root is a CLONE of the outgoing one,
    ///   taken while our scale was still applied. It is a genuinely new object with a new
    ///   id, it has never been touched by us, and it is already 1.25x. Measured live it
    ///   reads as vanilla, the config multiplies on top, and the rink comes out 1.5625x.
    ///   No amount of static bookkeeping can see that, because the evidence is not in our
    ///   state, it is in the object.
    ///
    /// Putting the baseline on the object fixes all three at once. A fresh object has no
    /// marker, so its current transform really is vanilla and gets recorded. A clone
    /// carries the marker along with the copied scale, so it already knows its vanilla
    /// value is 1.0 and never mistakes the inherited 1.25 for one. Our own statics can be
    /// dropped and rebuilt freely, because the truth lives elsewhere.
    /// </summary>
    internal sealed class ArenaBaselineMarker : MonoBehaviour
    {
        [SerializeField] private Vector3 _baseScale = Vector3.one;
        [SerializeField] private Vector3 _basePosition = Vector3.zero;

        internal Vector3 BaseScale => _baseScale;
        internal Vector3 BasePosition => _basePosition;

        /// <summary>
        /// Reads the object's vanilla transform, recording it on first sight. Returns the
        /// recorded values, which for a clone are the ones it inherited rather than
        /// whatever scale it currently carries.
        /// </summary>
        internal static ArenaBaselineMarker Resolve(Transform t, out bool captured)
        {
            captured = false;
            if (t == null) return null;

            var marker = t.GetComponent<ArenaBaselineMarker>();
            if (marker != null) return marker;

            marker = t.gameObject.AddComponent<ArenaBaselineMarker>();
            marker._baseScale = t.localScale;
            marker._basePosition = t.localPosition;
            captured = true;
            return marker;
        }

        /// <summary>
        /// Corrects a baseline that was measured from an already-resized object.
        ///
        /// The marker cannot help when the object arrives pre-scaled and unmarked, which
        /// is what a fresh scene load produced: a new level root, never touched by us, no
        /// marker to inherit, and already carrying the exact scale our config produces.
        /// The caller recognises that shape and supplies the real vanilla value.
        /// </summary>
        internal void OverrideBaseline(Vector3 baseScale, Vector3 basePosition)
        {
            _baseScale = baseScale;
            _basePosition = basePosition;
        }
    }
}
