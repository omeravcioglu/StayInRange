#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using EmeraldAI;
using EmeraldAI.Utility;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Emerald / creep detectible stand-in for a networked player. Lives on the replicated
    /// root so the host's AI can see a joining client, whose real FPS capsule only exists
    /// on that client's machine.
    /// </summary>
    [RequireComponent(typeof(FactionExtension))]
    public class NetworkedPlayerAiTarget : MonoBehaviour, IDamageable, ICombat
    {
        FpsNetworkBridge _bridge;
        TargetPositionModifier _tpm;
        readonly List<string> _activeEffects = new();

        public FpsNetworkBridge Bridge => _bridge;

        public int Health
        {
            get
            {
                if (!HasLiveBridge)
                    return 0;
                if (_bridge.IsDead)
                    return 0;
                int hp = Mathf.RoundToInt(_bridge.SyncedHealth + _bridge.SyncedShield);
                // SyncedHealth can still be 0 for a tick after spawn; treat a living player as visible.
                return hp > 0 ? hp : 1;
            }
            set { }
        }

        public int StartHealth
        {
            get => 100;
            set { }
        }

        public List<string> ActiveEffects
        {
            get => _activeEffects;
            set { }
        }

        bool HasLiveBridge =>
            _bridge != null && _bridge.Object != null && _bridge.Object.IsValid;

        public void Initialize(FpsNetworkBridge bridge)
        {
            _bridge = bridge;
            _tpm = GetComponent<TargetPositionModifier>();
        }

        void Awake()
        {
            if (_bridge == null)
                _bridge = GetComponentInParent<FpsNetworkBridge>();
            _tpm = GetComponent<TargetPositionModifier>();
        }

        public void Damage(int damageAmount, Transform attackerTransform = null, int ragdollForce = 100, bool criticalHit = false)
        {
            if (!HasLiveBridge || _bridge.IsDead)
                return;

            _bridge.TryRouteDamage(Mathf.Abs(damageAmount), criticalHit);

            if (CombatTextSystem.Instance != null)
                CombatTextSystem.Instance.CreateCombatText(damageAmount, DamagePosition(), criticalHit, false, true);
        }

        public Transform TargetTransform() => transform;

        public Vector3 DamagePosition()
        {
            if (_tpm != null && _tpm.TransformSource != null)
                return _tpm.TransformSource.position + Vector3.up * _tpm.PositionModifier;
            if (HasLiveBridge)
                return _bridge.GetNetworkAnchorPosition() + Vector3.up * 1.6f;
            return transform.position + Vector3.up;
        }

        public bool IsAttacking() => HasLiveBridge && _bridge.IsFiring;
        public bool IsBlocking() => false;
        public bool IsDodging() => false;
        public void TriggerStun(float stunLength) { }
    }
}
#endif
