using System.Collections.Generic;
using MalbersAnimations;
using MalbersAnimations.Reactions;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Lets Malbers' weapons and fists hurt an enemy. Malbers only damages objects that have its
    /// IMDamage, and the game's enemies take damage through DamageRouting (IDamageTarget), so this sits
    /// on an enemy's root and passes each Malbers hit on. The struck hitbox decides what takes the hit
    /// and whether it is critical (a head hitbox, tagged Critical); a critical hit does
    /// CriticalMultiplier times the damage, as the team's melee has always done.
    ///
    /// Being an IObjectCore, it is also what Malbers' attack triggers count as one enemy, so a swing
    /// through the head and the body hits once. A second hit from the same attacker within
    /// SameStrikeWindow is dropped as part of the same strike.
    ///
    /// Only enemies carry it (MarkEnemies, and the test dummies), so players never take Malbers damage.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class MalbersHitRelay : MonoBehaviour, IMDamage, IObjectCore
    {
        /// <summary>Damage multiplier for a hit on a critical hitbox (the head).</summary>
        public const float CriticalMultiplier = 2f;

        /// <summary>Hits from one attacker closer together than this are one strike.</summary>
        public const float SameStrikeWindow = 0.15f;

        readonly Dictionary<int, float> _lastHitBy = new Dictionary<int, float>();

        /// <summary>
        /// Every hit passed on: the relay, the struck object, the damage dealt, and whether it was critical.
        /// </summary>
        public static event System.Action<MalbersHitRelay, GameObject, float, bool> HitPassed;

        public Vector3 HitDirection { get; set; }
        public Vector3 HitPosition { get; private set; }
        public Transform Transform => transform;
        public GameObject Damager { get; set; }
        public GameObject Damagee => gameObject;
        public Collider HitCollider { get; set; }
        public ForceMode LastForceMode { get; set; }
        public SurfaceID Surface => null;

        public void ReceiveDamage(Vector3 Direction, Vector3 Position, GameObject Damager, StatModifier stat,
            bool IsCritical, Reaction2 custom, bool ignoreDamageeM, StatElement element, bool missed)
        {
            if (missed || stat.IsNull || stat.modify != StatOption.SubstractValue)
                return;

            HitDirection = Direction;
            HitPosition = Position;
            this.Damager = Damager;
            Pass(HitCollider != null ? HitCollider.gameObject : gameObject, stat.Value, IsCritical, Damager);
        }

        public void ReceiveDamage(StatID stat, float amount) => Pass(gameObject, amount, false, null);

        public void Profile_Set(string name) { }

        public void Profile_Restore() { }

        void Pass(GameObject struck, float amount, bool malbersCritical, GameObject attacker)
        {
            amount = Mathf.Abs(amount);
            if (amount <= 0f)
                return;

            if (attacker != null)
            {
                int key = attacker.GetInstanceID();
                if (_lastHitBy.TryGetValue(key, out float last) && Time.time - last < SameStrikeWindow)
                    return;
                _lastHitBy[key] = Time.time;
            }

            // Malbers' own critical (a chance on the weapon) has already multiplied its damage.
            bool hitboxCritical = IsCriticalHitbox(struck);
            if (hitboxCritical)
                amount *= CriticalMultiplier;
            bool critical = hitboxCritical || malbersCritical;

            if (DamageRouting.TryApply(struck, amount, critical))
                HitPassed?.Invoke(this, struck, amount, critical);
        }

        static bool IsCriticalHitbox(GameObject struck)
        {
            if (DamageRouting.IsCritical(struck))
                return true;
            var relay = struck.GetComponent<ZombieHitboxRelay>();
            return relay != null && relay.ForceHeadshot;
        }

        /// <summary>
        /// Puts a relay on every zombie in the scene that has none, spawned ones included. Cheap to
        /// repeat a couple of times a second.
        /// </summary>
        public static void MarkEnemies()
        {
            foreach (var zombie in FindObjectsByType<ZombieHealth>(FindObjectsSortMode.None))
            {
                if (zombie.GetComponent<MalbersHitRelay>() == null)
                    zombie.gameObject.AddComponent<MalbersHitRelay>();
            }
        }
    }
}
