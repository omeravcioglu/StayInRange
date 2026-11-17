using CollarCali.Combat;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.Scriptables;
using MalbersAnimations.Utilities;
using MalbersAnimations.Weapons;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fighting is Malbers' own: its fist and kick combos with nothing drawn, and its weapons (the
    /// sword first) through its weapon manager and holsters, on Malbers' keys - left and right mouse
    /// button (the gamepad triggers) attack, and 4, 5 and 6 draw and put away what is in each holster.
    /// This part only:
    ///   - puts the starting weapon in its holster when play starts, as Malbers' own player starts:
    ///     the fists fight first and 4 draws the sword;
    ///   - points every weapon and fist at the enemies' hitboxes, trigger colliders on the Enemy
    ///     layer, which Malbers' own set-up passes through, and keeps every zombie able to take
    ///     Malbers' hits (MalbersHitRelay passes them on to the game's damage);
    ///   - leaves out Malbers' air attacks (see RemoveAirLaunchers);
    ///   - mutes the attack keys while they mean something else (CanAttack says what).
    /// </summary>
    public partial class ThirdPersonPlayer
    {
        /// <summary>
        /// Malbers' attack and weapon keys: both attacks and the two together, reload, and the three
        /// holsters.
        /// </summary>
        public const string AttackInputs = "Action1,Action2,Action3,Reload,Holster1,Holster2,Holster3";

        /// <summary>Malbers' attack keys proper: both attacks and the two together.</summary>
        public const string StrikeInputs = "Action1,Action2,Action3";

        /// <summary>
        /// The states Malbers plays its air combos in. No attack starts in them: Malbers' air attacks
        /// push the body up hard (see RemoveAirLaunchers).
        /// </summary>
        static readonly string[] AirStates = { "Jump", "Fall", "Fly", "Glide", "Swim", "UnderWater" };

        /// <summary>Malbers' sword, Malbers Animations/Common/Prefabs/Weapons/Sword.prefab.</summary>
        public const string MalbersSwordGuid = "cd655bcb33b07a94c9e01c2e9b6b5573";

        [Header("Combat")]
        [Tooltip("A Malbers weapon prefab put in its holster when play starts: Malbers Animations/Common/Prefabs/Weapons/Sword. Its holster key draws it (4 for the first holster). Empty: fists only.")]
        [SerializeField] GameObject startingWeapon;

        [Tooltip("Have the starting weapon in hand when play starts. Off: it waits in its holster and the fists fight first, as Malbers' own player starts.")]
        [SerializeField] bool drawStartingWeapon;

        [Tooltip("What the player's weapons and fists hit. The enemies' hitboxes are on the Enemy layer.")]
        [SerializeField] LayerMask meleeHitLayers;

        /// <summary>
        /// Malbers' weapon manager prepares its holsters a moment after it wakes; the starting weapon
        /// goes in after that.
        /// </summary>
        const float StartingWeaponDelay = 0.1f;

        readonly Hold _attackKeys = new Hold("attack keys");
        readonly Hold _airKeys = new Hold("attack keys in the air");

        MWeaponManager _weapons;
        float _giveWeaponAt = -1f;
        float _nextEnemyScan;
        bool _attackKeysMuted;
        bool _airKeysMuted;
        int _releaseAttackFrame = -1;

        /// <summary>Malbers' weapon manager on this character: the weapon in hand, the holsters.</summary>
        public MWeaponManager Weapons => _weapons;

        /// <summary>Why the last TryAttack did not attack, or null if it did.</summary>
        public string LastAttackRefusal { get; private set; }

        int MeleeMask => meleeHitLayers.value != 0 ? meleeHitLayers.value : LayerMask.GetMask("Enemy");

        /// <summary>In one of the states Malbers plays air attacks in: jumping, falling, gliding, swimming.</summary>
        public bool InTheAir
        {
            get
            {
                var state = _animal != null ? _animal.ActiveStateID : null;
                return state != null && System.Array.IndexOf(AirStates, state.name) >= 0;
            }
        }

        void StartCombat()
        {
            _weapons = GetComponentInChildren<MWeaponManager>(true);
            if (_weapons != null)
            {
                // The weapon manager gives every weapon it puts in hand its own hit layers, after it says
                // the weapon is equipped; Malbers ships them without the Enemy layer.
                _weapons.OverrideWeaponLayer = new LayerReference(MeleeMask);
                _weapons.OnEquipWeapon.AddListener(OnWeaponEquipped);
            }

            ConfigureStrikers(gameObject);
            RemoveAirLaunchers();
            MalbersHitRelay.MarkEnemies();
            _giveWeaponAt = Time.time + StartingWeaponDelay;
        }

        void UpdateCombat()
        {
            SyncAttackKeys();

            // TryAttack is a click: the key goes up again a frame later.
            if (_releaseAttackFrame >= 0 && Time.frameCount >= _releaseAttackFrame)
            {
                _releaseAttackFrame = -1;
                if (_weapons != null)
                    _weapons.MainAttackReleased();
            }

            if (!_running)
                return;

            if (_giveWeaponAt >= 0f && Time.time >= _giveWeaponAt)
            {
                _giveWeaponAt = -1f;
                GiveStartingWeapon();
            }

            if (Time.unscaledTime >= _nextEnemyScan)
            {
                _nextEnemyScan = Time.unscaledTime + 0.5f;
                MalbersHitRelay.MarkEnemies();
            }
        }

        void GiveStartingWeapon()
        {
            if (_weapons == null || startingWeapon == null)
                return;
            var weapon = startingWeapon.GetComponent<MWeapon>();
            if (weapon == null)
            {
                Debug.LogWarning("[ThirdPersonPlayer] The starting weapon " + startingWeapon.name + " is not a Malbers weapon (no MWeapon on it).", this);
                return;
            }

            // A prefab: Malbers makes the copy and holsters it. Its holsters put a weapon in hand as it
            // arrives (auto-equip); unless the weapon is wanted drawn, that is held back for this one.
            var holster = drawStartingWeapon ? null : _weapons.holsters.Find(h => h != null && h.GetID == weapon.HolsterID);
            var autoEquip = holster?.AutoEquip;
            if (holster != null)
                holster.AutoEquip = new BoolReference(false);
            try
            {
                _weapons.Holster_SetWeapon(startingWeapon);
            }
            finally
            {
                if (holster != null)
                    holster.AutoEquip = autoEquip;
            }

            ConfigureStrikers(gameObject);
        }

        /// <summary>
        /// Malbers' air attacks push the body up hard - their Force Up modifier, 10 m/s for a second -
        /// which flies a player over a level's parkour, and the game starts without air attacks. Attacks
        /// are refused in the air (SyncAttackKeys), and these abilities leave the random pick Malbers
        /// falls back to when no combo fits, so a click on the ground never plays one either.
        /// </summary>
        void RemoveAirLaunchers()
        {
            var animal = Animal;
            if (animal == null)
                return;

            foreach (var mode in animal.modes)
            {
                if (mode?.Abilities == null)
                    continue;
                foreach (var ability in mode.Abilities)
                {
                    if (ability == null || !ability.IncludeInRandom || !(ability.modifier is ModifierForce force) ||
                        force.Direction.Value.y < 0.5f)
                        continue;
                    ability.IncludeInRandom = false;
                    mode.RandomAbilitiesIndex?.Remove(ability.Index.Value);
                }
            }
        }

        void OnWeaponEquipped(GameObject weapon)
        {
            if (weapon != null)
                ConfigureStrikers(weapon);
        }

        /// <summary>
        /// Malbers ships its weapons and fists hitting Default and a few layers of its own, and passing
        /// through trigger colliders. The enemies' hitboxes are triggers on the Enemy layer, so every
        /// striker under <paramref name="root"/> is pointed at those instead: its own filter, and the
        /// Malbers trigger proxy it listens through.
        /// </summary>
        void ConfigureStrikers(GameObject root)
        {
            int mask = MeleeMask;
            foreach (var damager in root.GetComponentsInChildren<MDamager>(true))
            {
                if (!(damager is MMelee) && !(damager is MAttackTrigger))
                    continue;

                // A constant of its own: a shared layer variable would change every Malbers weapon.
                damager.m_hitLayer = new LayerReference(mask);
                damager.TriggerInteraction = QueryTriggerInteraction.Collide;
                foreach (var proxy in damager.GetComponentsInChildren<TriggerProxy>(true))
                    AimProxy(proxy, mask);

                // The proxy sits on the striker's trigger collider, which need not be under it.
                var trigger = damager is MMelee melee ? melee.meleeTrigger : ((MAttackTrigger)damager).Trigger;
                if (trigger != null && trigger.TryGetComponent<TriggerProxy>(out var own))
                    AimProxy(own, mask);
            }
        }

        static void AimProxy(TriggerProxy proxy, int mask)
        {
            proxy.TriggerInteraction = QueryTriggerInteraction.Collide;
            proxy.Layer = mask;
        }

        /// <summary>
        /// Malbers reads its attack keys itself, so they are muted while they mean something else: in a
        /// menu, while aiming a throw, and in the frame a carry ends and the next, so the click that
        /// throws cannot also swing. Carrying mutes them already, with the rest of the throw's keys.
        /// The attacks themselves are also muted in the air.
        /// </summary>
        void SyncAttackKeys()
        {
            var locks = Locks;
            if (locks == null)
                return;

            bool take = _menuOpen || _heldByCreep || _aimViewRequested || _aimEyeLive || JustStoppedCarrying;
            if (take != _attackKeysMuted)
            {
                _attackKeysMuted = take;
                if (take)
                    locks.Mute(_attackKeys, AttackInputs);
                else
                    locks.Unmute(_attackKeys, AttackInputs);
            }

            bool air = InTheAir;
            if (air != _airKeysMuted)
            {
                _airKeysMuted = air;
                if (air)
                    locks.Mute(_airKeys, StrikeInputs);
                else
                    locks.Unmute(_airKeys, StrikeInputs);
            }
        }

        /// <summary>
        /// Whether the attack key is Malbers' right now. <paramref name="reason"/> says what has it
        /// otherwise.
        /// </summary>
        public bool CanAttack(out string reason)
        {
            var telekinesis = BodyTelekinesis.Local;
            var locks = _animal != null ? GetComponent<MAnimalLocks>() : null;
            reason = _suspended ? "downed"
                : _heldByCreep ? "held by a creep"
                : _menuOpen ? "a menu is open"
                : _carrying || JustStoppedCarrying ? "carrying"
                : _aimViewRequested || _aimEyeLive ? "aiming a throw"
                : InTheAir ? "in the air"
                : telekinesis != null && telekinesis.isActiveAndEnabled && telekinesis.IsHolding ? "telekinesis is holding something"
                : _weapons == null ? "no Malbers weapon manager"
                : !_weapons.Active ? "Malbers' weapon manager is off"
                : locks != null && locks.IsMuted("Action1") ? "the attack key is muted"
                : null;
            return reason == null;
        }

        /// <summary>
        /// A click of the attack key, for checks and tools: Malbers' main attack (a combo hit with the
        /// weapon in hand, or with the fists), unless the key is not Malbers' right now. The key goes up
        /// again a frame later: held, Malbers' sword keeps attacking. Malbers can still decline the
        /// attack: mid-draw, or while another move plays.
        /// </summary>
        public bool TryAttack()
        {
            if (!CanAttack(out string reason))
            {
                LastAttackRefusal = reason;
                return false;
            }

            LastAttackRefusal = null;
            _weapons.MainAttack();
            _releaseAttackFrame = Time.frameCount + 1;
            return true;
        }

        /// <summary>Stops an attack that is playing (going down mid-swing).</summary>
        void StopAttacks()
        {
            var animal = Animal;
            if (animal != null && animal.IsPlayingMode)
                animal.Mode_Interrupt();
        }
    }
}
