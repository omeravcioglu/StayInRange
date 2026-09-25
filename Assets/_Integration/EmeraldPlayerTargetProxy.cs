using System.Collections.Generic;
using EmeraldAI;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Forwards Emerald target queries from a player collider to the root target.
    /// </summary>
    public class EmeraldPlayerTargetProxy : MonoBehaviour, IDamageable, ICombat
    {
        CowsinsEmeraldPlayerTarget _root;

        CowsinsEmeraldPlayerTarget Root
        {
            get
            {
                if (_root == null)
                    _root = GetComponentInParent<CowsinsEmeraldPlayerTarget>();
                return _root;
            }
        }

        public int Health
        {
            get => Root != null ? Root.Health : 0;
            set { }
        }

        public int StartHealth
        {
            get => Root != null ? Root.StartHealth : 100;
            set { }
        }

        public List<string> ActiveEffects
        {
            get => Root != null ? Root.ActiveEffects : null;
            set { }
        }

        public void Damage(int damageAmount, Transform attackerTransform = null, int ragdollForce = 100, bool criticalHit = false)
        {
            Root?.Damage(damageAmount, attackerTransform, ragdollForce, criticalHit);
        }

        public Transform TargetTransform() => Root != null ? Root.TargetTransform() : transform;
        public Vector3 DamagePosition() => Root != null ? Root.DamagePosition() : transform.position + Vector3.up;
        public bool IsAttacking() => false;
        public bool IsBlocking() => false;
        public bool IsDodging() => false;
        public void TriggerStun(float stunLength) => Root?.TriggerStun(stunLength);
    }
}
