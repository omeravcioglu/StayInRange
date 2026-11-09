using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Finds what a struck collider belongs to and hurts it - the one lookup every player attack goes
    /// through, so a punch, a sword and a thrown prop all hit the same way.
    ///
    /// The collider's own object is checked first, then its parents: a hitbox relay on the collider
    /// wins over the health on the enemy's root, which is how a head hitbox makes a hit critical.
    /// </summary>
    public static class DamageRouting
    {
        /// <summary>Hitboxes with this tag take critical hits. The Enemy builders tag head hitboxes with it.</summary>
        public const string CriticalTag = "Critical";

        /// <summary>The damage target a struck object belongs to, or null.</summary>
        public static IDamageTarget Find(GameObject struck)
        {
            return struck != null ? struck.GetComponentInParent<IDamageTarget>() : null;
        }

        /// <summary>True for a hitbox tagged "Critical".</summary>
        public static bool IsCritical(GameObject struck)
        {
            return struck != null && struck.CompareTag(CriticalTag);
        }

        /// <summary>
        /// Hurts whatever <paramref name="struck"/> belongs to. Critical when asked, or when the
        /// struck hitbox is tagged "Critical"; the multiplier for a critical hit is the caller's to
        /// apply first. False when it belongs to nothing that can be hurt.
        /// </summary>
        public static bool TryApply(GameObject struck, float damage, bool critical = false)
        {
            var target = Find(struck);
            if (target == null)
                return false;

            target.Damage(Mathf.Abs(damage), critical || IsCritical(struck));
            return true;
        }
    }
}
