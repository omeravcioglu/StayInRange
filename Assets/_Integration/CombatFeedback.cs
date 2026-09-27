using System;
using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Hits and kills the local player causes, for the HUD: the hitmarker, the killfeed, and later
    /// the damage numbers.
    ///
    /// Cowsins only feeds its own hitmarker and killfeed from its own EnemyHealth, which none of the
    /// game's enemies use, so they never fired. The team's damage adapters (ZombieHealth,
    /// CreepHitsHealth, CowsinsHitsEmeraldHealth) report here instead, from the shooter's machine -
    /// before the damage is routed to whichever machine owns the enemy - so the shooter's feedback
    /// is immediate and never depends on who the master is.
    ///
    /// A kill is confirmed by watching: the target counts as this player's kill when it is found
    /// dead within a couple of seconds of their hit, which works whether the enemy's health lives
    /// on this machine or on the master's.
    /// </summary>
    public static class CombatFeedback
    {
        public readonly struct HitInfo
        {
            public readonly GameObject Target;
            public readonly float Damage;
            public readonly bool Headshot;
            public readonly Vector3 Position;

            public HitInfo(GameObject target, float damage, bool headshot, Vector3 position)
            {
                Target = target;
                Damage = damage;
                Headshot = headshot;
                Position = position;
            }
        }

        struct Watch
        {
            public GameObject Target;
            public Func<bool> IsDead;
            public HitInfo LastHit;
            public float Until;
        }

        const float KillWindowSeconds = 2f;

        static readonly List<Watch> Watching = new List<Watch>();

        /// <summary>This player landed a hit.</summary>
        public static event Action<HitInfo> Hit;

        /// <summary>Something this player hit died shortly afterwards. Carries the killing hit.</summary>
        public static event Action<HitInfo> Kill;

        /// <summary>
        /// The HUD is listening. Until it is - playing a level offline, say - the enemies still pop
        /// their own damage numbers the old way.
        /// </summary>
        public static bool HasListeners => Hit != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            Watching.Clear();
            Hit = null;
            Kill = null;
        }

        /// <summary>
        /// Reports a hit on <paramref name="target"/>. <paramref name="isDead"/> is asked later to
        /// confirm the kill; a target already dead is not reported at all.
        /// </summary>
        public static void ReportHit(GameObject target, float damage, bool headshot, Func<bool> isDead)
        {
            if (target == null || damage <= 0f || SafeIsDead(isDead))
                return;

            var info = new HitInfo(target, damage, headshot, target.transform.position);
            Hit?.Invoke(info);

            for (int i = Watching.Count - 1; i >= 0; i--)
            {
                if (Watching[i].Target == target)
                    Watching.RemoveAt(i);
            }

            Watching.Add(new Watch
            {
                Target = target,
                IsDead = isDead,
                LastHit = info,
                Until = Time.time + KillWindowSeconds,
            });
        }

        /// <summary>Confirms kills. Called once a frame by the HUD, the only thing that listens.</summary>
        public static void Tick()
        {
            for (int i = Watching.Count - 1; i >= 0; i--)
            {
                var watch = Watching[i];
                if (watch.Target == null)
                {
                    Watching.RemoveAt(i);
                    continue;
                }

                if (SafeIsDead(watch.IsDead))
                {
                    Watching.RemoveAt(i);
                    Kill?.Invoke(watch.LastHit);
                    continue;
                }

                if (Time.time > watch.Until)
                    Watching.RemoveAt(i);
            }
        }

        /// <summary>
        /// What to call an enemy in the killfeed: its prefab name without the clone suffix or
        /// trailing numbers, with the zombie variants named the way players know them.
        /// </summary>
        public static string DisplayName(GameObject target)
        {
            if (target == null)
                return "Something";

            var name = target.name.Replace("(Clone)", string.Empty).Trim();
            name = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '_');

            if (name.IndexOf("Crawler", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Crawler";
            if (name.IndexOf("Creep", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Creep";
            if (name.IndexOf("Magician", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Magician";
            if (name.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Zombie";
            return string.IsNullOrEmpty(name) ? "Something" : name;
        }

        static bool SafeIsDead(Func<bool> isDead)
        {
            if (isDead == null)
                return false;

            try
            {
                return isDead();
            }
            catch (Exception)
            {
                // A target torn down mid-check is not a kill worth crashing the HUD over.
                return false;
            }
        }
    }
}
