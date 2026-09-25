using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// HP-based damageable for zombies. Unlike CreepHitsHealth (fixed hits to die) this respects
    /// the weapon's own damage, so a shotgun drops a zombie in one and a pistol does not - which
    /// is what makes weapon choice matter against a horde.
    /// </summary>
    public class ZombieHealth : MonoBehaviour, cowsins.IDamageable
    {
        [Header("Health")]
        [SerializeField] float maxHealth = 100f;

        /// <summary>
        /// EXTRA multiplier on top of the weapon's own critical multiplier, which Cowsins has
        /// already applied by the time damage reaches here. Left at 1 so a headshot deals exactly
        /// what the weapon says - stacking a second 2.5x made pistol headshots delete a zombie.
        /// </summary>
        [SerializeField] float headshotMultiplier = 1f;

        [Header("Feedback")]
        [SerializeField] bool showDamageNumbers = true;
        /// <summary>
        /// Off by default. Cowsins already spawns the Enemy-layer impact - which BloodEffectBuilder
        /// points at the RVFX hit splash - for hitscan, melee and quick-action hits, and it does so
        /// at the exact hit point. Turning this on as well double-sprays those. Enable it if you
        /// want blood from damage that skips that path, such as explosions.
        /// </summary>
        [SerializeField] bool showBloodOnHit = false;

        /// <summary>The killing blow. Cowsins has no equivalent, so this is always ours.</summary>
        [SerializeField] bool showBloodOnDeath = true;

        /// <summary>Where a body hit sprays from, measured up the zombie's own transform.</summary>
        [SerializeField] float bodyHitHeight = 1.15f;

        public float MaxHealth => maxHealth;
        public float Health { get; private set; }
        public bool IsDead { get; private set; }

        /// <summary>Raised once, on the machine that owns the brain.</summary>
        public event System.Action OnDied;

        /// <summary>Raised on every damaging hit that does not kill. Argument is damage dealt.</summary>
        public event System.Action<float> OnHurt;

        bool _applyingNetworked;
        Transform _head;
        bool _headResolved;

        /// <summary>
        /// Set for scripted creatures that are not meant to be fought at all. Damage is dropped
        /// before anything else happens, so there are no damage numbers and no blood either - a
        /// number popping off something that cannot die reads as a broken enemy rather than an
        /// invincible one.
        /// </summary>
        bool _invulnerable;

        public bool IsInvulnerable => _invulnerable;

        public void SetInvulnerable(bool invulnerable)
        {
            _invulnerable = invulnerable;
        }

        void Awake()
        {
            maxHealth = Mathf.Max(1f, maxHealth);
            Health = maxHealth;
        }

        /// <summary>
        /// newHeadshotMultiplier is an EXTRA multiplier on top of the weapon's critical
        /// multiplier, which Cowsins has already applied. Pass 1 unless you deliberately
        /// want headshots to hit harder on zombies than on anything else.
        /// </summary>
        public void Configure(float newMaxHealth, float newHeadshotMultiplier)
        {
            maxHealth = Mathf.Max(1f, newMaxHealth);
            headshotMultiplier = Mathf.Max(1f, newHeadshotMultiplier);
            Health = maxHealth;
        }

        /// <summary>Cowsins entry point - every weapon hit lands here.</summary>
        public void Damage(float damage, bool isHeadshot)
        {
            if (_invulnerable)
                return;

            if (_applyingNetworked)
            {
                ApplyLocal(damage, isHeadshot);
                return;
            }

            if (showBloodOnHit && !IsDead && damage > 0f)
                SprayBlood(isHeadshot, ZombieBloodFx.Kind.Hit);

#if CMPSETUP_COMPLETE
            // In a session the master owns this zombie's health. Routing through the actor keeps
            // two clients shooting the same zombie from each subtracting from their own copy.
            var actor = NetworkWorldActor.FindFor(gameObject);
            if (actor != null && actor.Object != null && actor.Object.IsValid)
            {
                actor.RequestDamage(damage, isHeadshot);
                return;
            }
#endif

            ApplyLocal(damage, isHeadshot);
        }

        /// <summary>Called by the actor on the state authority once damage has been routed.</summary>
        public void ApplyNetworkedDamage(float damage, bool isHeadshot)
        {
            _applyingNetworked = true;
            try
            {
                ApplyLocal(damage, isHeadshot);
            }
            finally
            {
                _applyingNetworked = false;
            }
        }

        void ApplyLocal(float damage, bool isHeadshot)
        {
            if (IsDead || damage <= 0f)
                return;

            float dealt = isHeadshot ? damage * Mathf.Max(1f, headshotMultiplier) : damage;
            Health = Mathf.Max(0f, Health - dealt);

            if (showDamageNumbers)
                Scene2DamagePopup.Show(transform.position + Vector3.up * 1.6f, Mathf.RoundToInt(dealt));

            if (Health > 0f)
            {
                OnHurt?.Invoke(dealt);
                return;
            }

            IsDead = true;
            // Death is decided on the authority, so this burst is broadcast from there - unlike
            // the hit spray above, which fires on whoever pulled the trigger.
            if (showBloodOnDeath)
                SprayBlood(isHeadshot, ZombieBloodFx.Kind.Death);
            OnDied?.Invoke();
        }

        void SprayBlood(bool isHeadshot, ZombieBloodFx.Kind kind)
        {
            // Death sprays from the torso even on a headshot: the gut splash and the ground decal
            // both read better from centre mass than from where the head happened to be.
            bool fromHead = isHeadshot && kind == ZombieBloodFx.Kind.Hit;
            ZombieBloodFx.PlayShared(ResolveHitPoint(fromHead), kind);
        }

        /// <summary>
        /// Cowsins hands damage over without a hit position, so the spray is placed at the head
        /// bone for criticals and mid-torso otherwise. Close enough to read correctly, and it
        /// tracks the animation because the head bone moves.
        /// </summary>
        Vector3 ResolveHitPoint(bool isHeadshot)
        {
            if (isHeadshot)
            {
                if (!_headResolved)
                {
                    _headResolved = true;
                    var animator = GetComponentInChildren<Animator>();
                    if (animator != null && animator.isHuman)
                        _head = animator.GetBoneTransform(HumanBodyBones.Head);
                }

                if (_head != null)
                    return _head.position;
            }

            return transform.position + Vector3.up * bodyHitHeight;
        }
    }

    /// <summary>
    /// Fallback damageable on the hitbox objects themselves.
    ///
    /// Cowsins' own hit path resolves damage with GatherDamageableParent, which starts at the
    /// collider's PARENT and so finds ZombieHealth on the root without ever touching this. This
    /// exists for anything that calls Damage on the collider's own GameObject directly, and to
    /// keep a head hit counted as critical if it arrives that way.
    /// </summary>
    public class ZombieHitboxRelay : MonoBehaviour, cowsins.IDamageable
    {
        public ZombieHealth Root;

        /// <summary>Set on the head hitbox so this collider always counts as a headshot.</summary>
        public bool ForceHeadshot;

        public void Damage(float damage, bool isHeadshot)
        {
            if (Root == null)
                Root = GetComponentInParent<ZombieHealth>();
            Root?.Damage(damage, isHeadshot || ForceHeadshot);
        }
    }
}
