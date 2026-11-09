using System;
using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// A player's health - the third-person player's replacement for Cowsins' PlayerStats. It lives
    /// on the local player's character, so the same component works in a session and offline.
    ///
    /// WHO DECIDES. The owning machine, as before. In a session the network bridge sets Router:
    /// damage arriving here goes through it (to the owner's machine), and the owner's bridge applies
    /// it with ApplyLocal and mirrors Health and IsDead to everyone else. Offline there is no router,
    /// and damage applies straight away.
    ///
    /// One pool, no shield: Cowsins gave 100 health and a 50 shield that took damage first, so the
    /// default maximum of 150 (PlayerTuning > Vitals) keeps every enemy exactly as deadly as it was.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerVitals : MonoBehaviour, IDamageTarget
    {
        /// <summary>Every enabled player's vitals on this machine.</summary>
        public static readonly List<PlayerVitals> All = new List<PlayerVitals>();

        /// <summary>
        /// The local player's vitals, or null. Only the local player has a character with vitals -
        /// other players are drawn from replicated state - so this is the most recently enabled one.
        /// </summary>
        public static PlayerVitals Local { get; private set; }

        float _maxHealth;
        float _invulnerableUntil = float.NegativeInfinity;
        bool _initialised;

        /// <summary>Sends damage to the machine that owns this player. Set by the network bridge; null offline.</summary>
        public INetworkedDamageRouter Router { get; set; }

        public float MaxHealth
        {
            get
            {
                EnsureInitialised();
                return _maxHealth;
            }
        }

        public float Health { get; private set; }

        /// <summary>Health as a fraction of the maximum, 0..1.</summary>
        public float Normalized => MaxHealth > 0f ? Mathf.Clamp01(Health / MaxHealth) : 0f;

        public bool IsDead { get; private set; }

        /// <summary>True for a moment after a revive or respawn, so nobody is killed again while getting up.</summary>
        public bool IsInvulnerable => Time.time < _invulnerableUntil;

        /// <summary>Health was taken. Raised after the health changes, before Died.</summary>
        public event Action<DamageInfo> Damaged;

        /// <summary>Health reached zero.</summary>
        public event Action<DamageInfo> Died;

        /// <summary>Brought back from dead by a revive or a respawn.</summary>
        public event Action Revived;

        /// <summary>Any change to Health, with the new value.</summary>
        public event Action<float> HealthChanged;

        void Awake()
        {
            EnsureInitialised();
        }

        void OnEnable()
        {
            if (!All.Contains(this))
                All.Add(this);
            Local = this;
        }

        void OnDisable()
        {
            All.Remove(this);
            if (Local == this)
                Local = All.Count > 0 ? All[All.Count - 1] : null;
        }

        void EnsureInitialised()
        {
            if (_initialised)
                return;
            _initialised = true;

            _maxHealth = Mathf.Max(1f, PlayerTuning.Active.vitals.maxHealth);
            Health = _maxHealth;
        }

        /// <summary>IDamageTarget: a hit from anything, on any machine.</summary>
        public void Damage(float damage, bool isHeadshot)
        {
            Damage(new DamageInfo(Mathf.Abs(damage), isHeadshot));
        }

        /// <summary>A hit with its details. Goes to the owning machine first when there is a router.</summary>
        public void Damage(DamageInfo info)
        {
            if (Router != null && Router.TryRouteDamage(info.Amount, info.Critical))
                return;

            ApplyLocal(info);
        }

        /// <summary>
        /// Applies a hit here and now. For the owning machine (the bridge calls it) and offline; it
        /// does not route anywhere.
        /// </summary>
        public void ApplyLocal(DamageInfo info)
        {
            EnsureInitialised();
            if (IsDead || IsInvulnerable || info.Amount <= 0f)
                return;

            Health = Mathf.Max(0f, Health - info.Amount);
            Damaged?.Invoke(info);
            HealthChanged?.Invoke(Health);

            if (Health <= 0f)
                Die(info);
        }

        public void Heal(float amount)
        {
            EnsureInitialised();
            if (IsDead || amount <= 0f || Health >= _maxHealth)
                return;

            Health = Mathf.Min(_maxHealth, Health + amount);
            HealthChanged?.Invoke(Health);
        }

        /// <summary>Kills outright, whatever the health - a collar failure, a kill volume.</summary>
        public void Kill(DamageInfo info)
        {
            EnsureInitialised();
            if (IsDead)
                return;

            Health = 0f;
            HealthChanged?.Invoke(Health);
            Die(info);
        }

        /// <summary>
        /// Back from dead with <paramref name="healthFraction"/> of the maximum, invulnerable for
        /// PlayerTuning > Vitals > Invulnerable After Revive seconds. Also refills a living player.
        /// </summary>
        public void Revive(float healthFraction = 1f)
        {
            EnsureInitialised();
            bool wasDead = IsDead;

            IsDead = false;
            Health = Mathf.Max(1f, _maxHealth * Mathf.Clamp01(healthFraction));
            _invulnerableUntil = Time.time + PlayerTuning.Active.vitals.invulnerableAfterRevive;
            HealthChanged?.Invoke(Health);

            if (wasDead)
                Revived?.Invoke();
        }

        void Die(DamageInfo info)
        {
            IsDead = true;
            Died?.Invoke(info);
        }
    }
}
