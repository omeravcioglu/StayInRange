using System;
using System.Collections.Generic;
using GameCreator.Runtime.Melee;
using MalbersAnimations;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// The melee half of the bridge: when a Game Creator attack may start, what it locks while it
    /// plays, what cancels it, and how its hits become damage.
    ///
    ///   - An attack starts only on the ground, in Malbers' Idle or Locomotion, with no Malbers mode
    ///     playing (TryAttack; CanMelee says why not): no attacks while climbing, wall running,
    ///     hanging from a ledge, on a ladder, swimming, in the air, or in a Malbers action or hit.
    ///   - While it plays, Malbers' movement and input are locked: the stick does not walk the body
    ///     out of a swing and Jump does not cut it short. The skill's own lunge and warp still move it
    ///     (root motion and the driver). Only locks this set are released afterwards.
    ///   - Leaving the ground, any other Malbers state, or a Malbers mode starting (a hit reaction)
    ///     cancels it at once. One started some other way while not allowed is cancelled the same way.
    ///   - Its hits: Game Creator runs a skill's On Hit once per struck collider, so a blade crossing a
    ///     zombie's head and body hits it twice. InstructionTeamDealDamage hands each hit here instead,
    ///     and at the end of the frame each enemy takes one hit per strike - critical if any of its
    ///     struck colliders was - through DamageRouting. Players are never hurt (friendly fire off).
    /// </summary>
    public partial class MAnimalGC2Bridge
    {
        /// <summary>One hit that was applied, for the checks and for feedback (hit markers, sounds).</summary>
        public readonly struct MeleeHit
        {
            public readonly GameObject Struck;
            public readonly Component Enemy;
            public readonly float Damage;
            public readonly bool Critical;
            public readonly int Strike;

            public MeleeHit(GameObject struck, Component enemy, float damage, bool critical, int strike)
            {
                Struck = struck;
                Enemy = enemy;
                Damage = damage;
                Critical = critical;
                Strike = strike;
            }
        }

        /// <summary>Raised for every hit applied, after the enemy has taken it.</summary>
        public event Action<MeleeHit> HitApplied;

        /// <summary>Raised when an attack is cancelled by these rules, with the reason.</summary>
        public event Action<string> AttackCancelled;

        /// <summary>Game Creator's melee on this character (made on first use).</summary>
        public MeleeStance Melee => Character != null ? Character.Combat.RequestStance<MeleeStance>() : null;

        /// <summary>True from an attack's anticipation to the end of its recovery.</summary>
        public bool IsAttacking { get; private set; }

        /// <summary>How many strikes (swings reaching their strike phase) so far.</summary>
        public int Strikes { get; private set; }

        /// <summary>Why the last TryAttack was refused, or null.</summary>
        public string LastRefusal { get; private set; }

        /// <summary>Why the last attack was cancelled by these rules, or null.</summary>
        public string LastCancel { get; private set; }

        MeleePhase _phase = MeleePhase.None;
        bool _lockedMovement, _lockedInput;

        struct PendingHit
        {
            public GameObject Struck;
            public Component Enemy;
            public float Damage, CriticalMultiplier;
            public bool Critical;
        }

        // Hits waiting for the end of the frame (one per enemy), and the enemies this strike has hit.
        readonly Dictionary<int, PendingHit> _pending = new Dictionary<int, PendingHit>();
        readonly HashSet<int> _hitThisStrike = new HashSet<int>();
        readonly List<PendingHit> _applying = new List<PendingHit>();

        MeleePhase CurrentPhase
        {
            get
            {
                var melee = Character != null ? Melee : null;
                return melee != null ? melee.CurrentPhase : MeleePhase.None;
            }
        }

        static bool IsAttackPhase(MeleePhase phase) =>
            phase == MeleePhase.Charge || phase == MeleePhase.Anticipation ||
            phase == MeleePhase.Strike || phase == MeleePhase.Recovery;

        /// <summary>
        /// Whether an attack may start (or a combo continue) now; <paramref name="reason"/> says why
        /// not. A press during a swing is fine: Game Creator buffers it for the next hit of the combo.
        /// The same rules keep an attack going.
        /// </summary>
        public bool CanMelee(out string reason)
        {
            reason = null;
            if (Animal == null || Character == null || !Animal.enabled)
                reason = "no character";
            else if (Animal.Sleep)
                reason = "the character is asleep";
            else if (!Animal.Grounded)
                reason = "in the air";
            else if (Animal.ActiveStateID == null ||
                     (Animal.ActiveStateID.ID != StateEnum.Idle && Animal.ActiveStateID.ID != StateEnum.Locomotion))
                reason = "in Malbers' " + (Animal.ActiveStateID != null ? Animal.ActiveStateID.name : "no") + " state";
            else if (Animal.IsPlayingMode)
                reason = "playing Malbers' " + (Animal.ActiveMode?.ID != null ? Animal.ActiveMode.ID.name : "") + " mode";
            return reason == null;
        }

        /// <summary>
        /// Presses melee key <paramref name="key"/> (Game Creator's combo input) when an attack may
        /// start, or continues a combo in its recovery. False, with LastRefusal, when it may not.
        /// </summary>
        public bool TryAttack(MeleeKey key)
        {
            if (!CanMelee(out string reason))
            {
                LastRefusal = reason;
                return false;
            }

            LastRefusal = null;
            Melee?.InputExecute(key);
            return true;
        }

        /// <summary>A new strike resets which enemies it has hit. Safe to call more than once a frame.</summary>
        void SyncStrike()
        {
            var phase = CurrentPhase;
            if (phase == MeleePhase.Strike && _phase != MeleePhase.Strike)
            {
                Strikes++;
                _hitThisStrike.Clear();
            }

            _phase = phase;
        }

        void TrackMelee()
        {
            if (Character == null || Animal == null)
                return;

            SyncStrike();
            bool attacking = IsAttackPhase(_phase);
            if (attacking && !CanMelee(out string why))
            {
                Melee?.ForceCancel();
                Clear(); // the lunge or warp it asked for this frame must not move the body either
                LastCancel = why;
                AttackCancelled?.Invoke(why);
                attacking = false;
                _phase = CurrentPhase;
            }

            if (attacking == IsAttacking)
                return;

            IsAttacking = attacking;
            if (attacking)
                Lock();
            else
                Unlock();
        }

        void Lock()
        {
            _lockedMovement = !Animal.LockMovement;
            if (_lockedMovement)
                Animal.LockMovement = true;
            _lockedInput = !Animal.LockInput;
            if (_lockedInput)
                Animal.LockInput = true;
        }

        void Unlock()
        {
            if (Animal != null)
            {
                if (_lockedMovement)
                    Animal.LockMovement = false;
                if (_lockedInput)
                    Animal.LockInput = false;
            }

            _lockedMovement = _lockedInput = false;
        }

        void ReleaseMelee()
        {
            Unlock();
            IsAttacking = false;
            _pending.Clear();
        }

        /// <summary>
        /// Takes one struck collider of a melee hit (InstructionTeamDealDamage). Each enemy takes at
        /// most one hit per strike, critical if any collider of it struck by that strike is critical.
        /// </summary>
        public void QueueHit(GameObject struck, float damage, float criticalMultiplier)
        {
            if (struck == null || TeamCanHit.IsPlayer(struck.transform))
                return;

            SyncStrike();
            var enemy = EnemyOf(struck);
            if (enemy == null)
                return;

            int key = enemy.GetInstanceID();
            if (_hitThisStrike.Contains(key))
                return;

            bool critical = IsCritical(struck);
            if (_pending.TryGetValue(key, out var queued) && (queued.Critical || !critical))
                return;

            _pending[key] = new PendingHit
            {
                Struck = struck,
                Enemy = enemy,
                Damage = Mathf.Abs(damage),
                CriticalMultiplier = Mathf.Max(1f, criticalMultiplier),
                Critical = critical,
            };
        }

        void ApplyHits()
        {
            if (_pending.Count == 0)
                return;

            _applying.Clear();
            _applying.AddRange(_pending.Values);
            _pending.Clear();
            foreach (var hit in _applying)
            {
                if (hit.Struck == null || hit.Enemy == null)
                    continue;

                float amount = hit.Damage * (hit.Critical ? hit.CriticalMultiplier : 1f);
                if (!DamageRouting.TryApply(hit.Struck, amount, hit.Critical))
                    continue;

                _hitThisStrike.Add(hit.Enemy.GetInstanceID());
                HitApplied?.Invoke(new MeleeHit(hit.Struck, hit.Enemy, amount, hit.Critical, Strikes));
            }
        }

        /// <summary>The enemy a struck collider belongs to: its outermost damage target (a zombie's health, not its hitbox's relay).</summary>
        static Component EnemyOf(GameObject struck)
        {
            var relay = struck.GetComponent<ZombieHitboxRelay>();
            if (relay != null && relay.Root != null)
                return relay.Root;

            var targets = struck.GetComponentsInParent<IDamageTarget>();
            return targets.Length > 0 ? targets[targets.Length - 1] as Component : null;
        }

        static bool IsCritical(GameObject struck)
        {
            if (DamageRouting.IsCritical(struck))
                return true;
            var relay = struck.GetComponent<ZombieHitboxRelay>();
            return relay != null && relay.ForceHeadshot;
        }
    }
}
