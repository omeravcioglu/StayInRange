using System.Collections.Generic;
using cowsins;
using EmeraldAI;
using EmeraldAI.Utility;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Emerald target that applies incoming AI damage to Cowsins PlayerStats.
    /// EmeraldGeneralTargetBridge keeps its own HP and never touches the FPS player.
    /// </summary>
    [RequireComponent(typeof(FactionExtension))]
    [RequireComponent(typeof(TargetPositionModifier))]
    public class CowsinsEmeraldPlayerTarget : MonoBehaviour, EmeraldAI.IDamageable, ICombat
    {
        PlayerStats _stats;
        TargetPositionModifier _tpm;
        readonly List<string> _activeEffects = new();

        public int Health
        {
            get
            {
                ResolveStats();
                return _stats != null ? Mathf.RoundToInt(_stats.health + _stats.shield) : 0;
            }
            set { }
        }

        public int StartHealth
        {
            get => _stats != null ? Mathf.RoundToInt(_stats.maxHealth + _stats.maxShield) : 100;
            set { }
        }

        public List<string> ActiveEffects
        {
            get => _activeEffects;
            set { }
        }

        void Awake()
        {
            ResolveStats();
            _tpm = GetComponent<TargetPositionModifier>();
        }

        void ResolveStats()
        {
            if (_stats != null)
                return;
            _stats = GetComponent<PlayerStats>()
                ?? GetComponentInParent<PlayerStats>()
                ?? GetComponentInChildren<PlayerStats>(true);
        }

        public void Damage(int damageAmount, Transform attackerTransform = null, int ragdollForce = 100, bool criticalHit = false)
        {
            ResolveStats();
            if (_stats == null || _stats.IsDead)
                return;

            _stats.Damage(Mathf.Abs(damageAmount), criticalHit);

            if (CombatTextSystem.Instance != null)
                CombatTextSystem.Instance.CreateCombatText(damageAmount, DamagePosition(), criticalHit, false, true);
        }

        public Transform TargetTransform()
        {
            return transform;
        }

        public Vector3 DamagePosition()
        {
            if (_tpm != null && _tpm.TransformSource != null)
                return _tpm.TransformSource.position + Vector3.up * _tpm.PositionModifier;
            return transform.position + Vector3.up;
        }

        public bool IsAttacking() => false;
        public bool IsBlocking() => false;
        public bool IsDodging() => false;
        public void TriggerStun(float stunLength) { }
    }
}
