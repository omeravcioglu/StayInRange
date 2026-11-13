using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Marks a collider Game Creator's melee may hit - without one, Game Creator never runs a skill's
    /// hit instructions on it, so props stay untouched. Goes on each hitbox collider of an enemy (a
    /// zombie's head and body, a test dummy's).
    ///
    /// Friendly fire is off: a player's own body and other players are refused. So is anything that
    /// has nothing to hurt, so a strike's damage always has somewhere to go (DamageRouting).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CollarCali/Combat/Team Can Hit")]
    public class TeamCanHit : CanHit
    {
        public override bool AllowHits(Character attacker)
        {
            if (!isActiveAndEnabled || attacker == null)
                return false;
            if (transform.IsChildOf(attacker.transform))
                return false;
            if (IsPlayer(transform))
                return false;
            return DamageRouting.Find(gameObject) != null;
        }

        /// <summary>A player's body: the spike's GC2 bridge, or the player's own vitals.</summary>
        public static bool IsPlayer(Transform t) =>
            t != null && (t.GetComponentInParent<MAnimalGC2Bridge>() != null || t.GetComponentInParent<PlayerVitals>() != null);
    }
}
