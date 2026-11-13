#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CollarCali.Combat;
using CollarCali.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;
using GC2Args = GameCreator.Runtime.Common.Args;
using MeleeKey = GameCreator.Runtime.Melee.MeleeKey;
using MeleePhase = GameCreator.Runtime.Melee.MeleePhase;
using MeleeWeapon = GameCreator.Runtime.Melee.MeleeWeapon;
using Striker = GameCreator.Runtime.Melee.Striker;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// The live check's melee part, on a player with Game Creator melee (MAnimalGC2Bridge): it equips
    /// the team sword (MeleeContentBuilder) and checks the bridge's rules and the sword's hits.
    ///   - The attack locks: an attack with the stick held and Jump pressed must neither walk nor jump.
    ///   - Refusals: no attack in the air or during a Malbers mode; one forced in the air is cancelled.
    ///   - Leaving the ground mid-attack cancels it within 0.15 s.
    ///   - Hits: 20 strikes on a dummy with a body and a head must each hit it once (one damage per
    ///     strike, though the blade crosses both hitboxes); a head-only dummy's hits must all be
    ///     critical; a plain prop must never be hurt.
    /// </summary>
    public static partial class CharacterLiveCheck
    {
        partial class Probe
        {
            enum MeleeStep { Equip, Lock, AirRefuse, GroundCancel, ModeRefuse, SwordHits, HeadHits, PropHit, Zombie, Teammate, Done }

            const int SwordStrikes = 20, HeadStrikes = 10, PropStrikes = 3;

            /// <summary>Where a target stands for the strikes: in front of the player, at this distance (m).</summary>
            const float TargetDistance = 1.1f;

            /// <summary>Leaving the ground mid-attack must cancel it within this long (s).</summary>
            const float CancelLimit = 0.15f;

            MeleeStep _meleeStep = MeleeStep.Equip;
            float _meleeStepStart = -1f;
            bool _stepStarted, _meleeHooked;
            float InStep => Now - _meleeStepStart;

            readonly List<(float time, string reason)> _cancels = new List<(float, string)>();

            // Game Creator's stock skills and dodge change the global time scale; the team's must not.
            float _minTimeScale = float.MaxValue, _maxTimeScale = float.MinValue;

            MeleeWeapon _sword;
            string _swordNote;

            // The lock test.
            bool _lockStarted, _lockSawAttack, _lockLocked, _lockJumped;
            string _lockRefusal;
            Vector3 _lockFrom;
            float _lockMoved;

            // In the air.
            bool _airTried, _airRefused, _airStruck;
            string _airRefusal, _airCancel;
            float _airForcedAt = -1f;
            int _airStrikes;

            // Lifted off the ground mid-attack.
            bool _liftStarted, _lifted;
            float _liftedAt = -1f, _airborneAt = -1f, _liftCancelledAt = -1f;
            string _liftReason;

            // During a Malbers mode.
            bool _modeTried, _modeRefused;
            string _modeRefusal, _modeNote;

            class StrikeTally
            {
                public string label;
                public int wanted, strikes, struck, doubles, hits, criticals, startStrikes, targetHits;
                public float targetDamage;
                public readonly Dictionary<int, int> hitsPerStrike = new Dictionary<int, int>();
            }

            readonly List<StrikeTally> _tallies = new List<StrikeTally>();
            StrikeTally _tally;
            GameObject _target;
            MeleeTestDummy _dummy;
            MeleePhase _lastMeleePhase = MeleePhase.None;

            /// <summary>One Editor frame of the melee part; true when it is over.</summary>
            bool StepMelee()
            {
                if (_meleeStepStart < 0f)
                    _meleeStepStart = Now;
                _minTimeScale = Mathf.Min(_minTimeScale, Time.timeScale);
                _maxTimeScale = Mathf.Max(_maxTimeScale, Time.timeScale);
                if (!_meleeHooked)
                {
                    _meleeHooked = true;
                    _bridge.HitApplied += OnMeleeHit;
                    _bridge.AttackCancelled += reason => _cancels.Add((Now, reason));
                }

                switch (_meleeStep)
                {
                    case MeleeStep.Equip:
                        if (StepEquip())
                            NextStep(_sword != null ? MeleeStep.Lock : MeleeStep.Done);
                        break;
                    case MeleeStep.Lock:
                        if (StepLock())
                            NextStep(MeleeStep.AirRefuse);
                        break;
                    case MeleeStep.AirRefuse:
                        if (StepAirRefuse())
                            NextStep(MeleeStep.GroundCancel);
                        break;
                    case MeleeStep.GroundCancel:
                        if (StepGroundCancel())
                            NextStep(MeleeStep.ModeRefuse);
                        break;
                    case MeleeStep.ModeRefuse:
                        if (StepModeRefuse())
                            NextStep(MeleeStep.SwordHits);
                        break;
                    case MeleeStep.SwordHits:
                        if (StepStrikes("Dummy, body and head", SwordStrikes, () => Dummy(head: true, body: true, headHeight: 1.62f)))
                            NextStep(MeleeStep.HeadHits);
                        break;
                    case MeleeStep.HeadHits:
                        if (StepStrikes("Dummy, head only (at chest height)", HeadStrikes, () => Dummy(head: true, body: false, headHeight: 1.25f)))
                            NextStep(MeleeStep.PropHit);
                        break;
                    case MeleeStep.PropHit:
                        if (StepStrikes("Plain prop (a crate)", PropStrikes, Crate))
                            NextStep(MeleeStep.Zombie);
                        break;
                    case MeleeStep.Zombie:
                        if (StepZombie())
                            NextStep(MeleeStep.Teammate);
                        break;
                    case MeleeStep.Teammate:
                        if (StepTeammate())
                            NextStep(MeleeStep.Done);
                        break;
                    case MeleeStep.Done:
                        _bridge.HitApplied -= OnMeleeHit;
                        return true;
                }

                return false;
            }

            void NextStep(MeleeStep step)
            {
                _meleeStep = step;
                _meleeStepStart = Now;
                _stepStarted = false;
            }

            bool IsAttackPhase(MeleePhase phase) =>
                phase == MeleePhase.Charge || phase == MeleePhase.Anticipation ||
                phase == MeleePhase.Strike || phase == MeleePhase.Recovery;

            MeleePhase MeleeNow => _bridge.Melee != null ? _bridge.Melee.CurrentPhase : MeleePhase.None;

            static float Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up).magnitude;

            // ---- Equip --------------------------------------------------------------------------

            bool StepEquip()
            {
                if (!_stepStarted)
                {
                    _stepStarted = true;
                    _sword = FindAsset<MeleeWeapon>("TeamSword_Weapon");
                    if (_sword == null)
                    {
                        _failures.Add("Melee: TeamSword_Weapon not found (Tools > CollarCali > Character > GC2 Melee Spike > Build Team Melee Content).");
                        return true;
                    }

                    _ = _gc2.Combat.Equip(_sword, null, new GC2Args(_gc2.gameObject));
                    return false;
                }

                if (InStep < 1.2f)
                    return false;

                var blade = _animal.GetComponentsInChildren<Striker>(true).FirstOrDefault();
                bool equipped = _gc2.Combat.IsEquipped(_sword);
                if (!equipped || blade == null)
                {
                    _failures.Add($"Melee: the team sword did not equip (equipped {equipped}, blade striker {(blade != null ? "found" : "missing")}).");
                    _sword = null;
                    return true;
                }

                // Game Creator's blade: a capsule 0.8 m long, centred 0.5 m along the prop's X.
                var root = _animal.transform;
                var t = blade.transform;
                var tip = root.InverseTransformPoint(t.TransformPoint(new Vector3(0.9f, 0f, 0f)));
                var hilt = root.InverseTransformPoint(t.TransformPoint(new Vector3(0.1f, 0f, 0f)));
                var hand = _hand != null ? root.InverseTransformPoint(_hand.position) : hilt;
                var spikeInput = _animal.GetComponent<CollarCali.Spikes.GC2SpikeSwordInput>();
                if (spikeInput != null)
                    _notes.Add("Spike sword input on the player: Malbers' attack inputs taken: " +
                               (string.IsNullOrEmpty(spikeInput.TakenInputs) ? "none" : spikeInput.TakenInputs) + ".");
                string holder = _hand != null && t.IsChildOf(_hand) ? "the right hand" : PathOf(t.parent);
                _swordNote = $"Team sword held by {holder}, its blade tip {(tip - hand).magnitude:0.00} m from the hand " +
                             $"({(tip - hilt).magnitude:0.00} m of blade).";
                return true;
            }

            // ---- The attack locks -----------------------------------------------------------------

            /// <summary>An attack with the stick held forward and Jump pressed: it must neither walk nor jump.</summary>
            bool StepLock()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    _lockFrom = _animal.transform.position;
                    Move(new Vector3(0f, 0f, 1f));
                    _lockStarted = _bridge.TryAttack(MeleeKey.A);
                    _lockRefusal = _bridge.LastRefusal;
                    if (!_lockStarted)
                    {
                        Move(Vector3.zero);
                        return true;
                    }

                    return false;
                }

                if (!_held && InStep > 0.25f && InStep < 0.3f)
                    Press(true);
                if (_held && InStep > 0.4f)
                    Press(false);

                if (_bridge.IsAttacking)
                {
                    _lockSawAttack = true;
                    if (_animal.LockMovement && _animal.LockInput)
                        _lockLocked = true;
                    string state = StateName();
                    if (state == "Jump" || state == "Fall")
                        _lockJumped = true;
                    _lockMoved = Mathf.Max(_lockMoved, Flat(_animal.transform.position - _lockFrom));
                }

                if ((_lockSawAttack && !_bridge.IsAttacking) || InStep > 5f)
                {
                    Move(Vector3.zero);
                    if (_held)
                        Press(false);
                    return true;
                }

                return false;
            }

            /// <summary>In the air: an attack is refused, and one started anyway is cancelled before it strikes.</summary>
            bool StepAirRefuse()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    Press(true);
                    return false;
                }

                if (_held && InStep > 0.12f)
                    Press(false);

                if (!_airTried && !_animal.Grounded && InStep > 0.25f)
                {
                    _airTried = true;
                    _airRefused = !_bridge.TryAttack(MeleeKey.A);
                    _airRefusal = _bridge.LastRefusal;

                    // Started some other way: the rules must cancel it.
                    _airStrikes = _bridge.Strikes;
                    _airForcedAt = Now;
                    _bridge.Melee?.InputExecute(MeleeKey.A);
                }

                if (_airForcedAt >= 0f)
                {
                    if (_bridge.Strikes > _airStrikes)
                        _airStruck = true;
                    if (_airCancel == null)
                        _airCancel = _cancels.Where(c => c.time >= _airForcedAt).Select(c => c.reason).FirstOrDefault();
                }

                return (_airTried && Standing() && InStep > 0.6f) || InStep > 6f;
            }

            /// <summary>An attack whose body is lifted off the ground in its anticipation must be cancelled quickly.</summary>
            bool StepGroundCancel()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    _liftStarted = _bridge.TryAttack(MeleeKey.A);
                    return !_liftStarted;
                }

                if (!_lifted && MeleeNow == MeleePhase.Anticipation && InStep > 0.08f)
                {
                    _lifted = true;
                    _liftedAt = Now;
                    _animal.Teleport(_animal.transform.position + _animal.transform.up * 1.2f);
                }

                // Off the ground: Malbers falling, or no longer grounded - whichever it reports first.
                string state = StateName();
                if (_lifted && _airborneAt < 0f && (!_animal.Grounded || state == "Fall" || state == "Jump"))
                    _airborneAt = Now;
                if (_lifted && _liftCancelledAt < 0f && !IsAttackPhase(MeleeNow))
                {
                    _liftCancelledAt = Now;
                    _liftReason = _cancels.Where(c => c.time >= _liftedAt).Select(c => c.reason).FirstOrDefault();
                }

                return (_liftCancelledAt >= 0f && Standing() && InStep > 0.6f) || InStep > 6f;
            }

            /// <summary>During one of Malbers' modes (its own fist attack), an attack is refused.</summary>
            bool StepModeRefuse()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    if (_mode == null || !_animal.Mode_TryActivate(_mode.ID.ID))
                    {
                        _modeNote = "no Malbers mode could start";
                        return true;
                    }

                    return false;
                }

                if (!_modeTried && _animal.IsPlayingMode)
                {
                    _modeTried = true;
                    _modeRefused = !_bridge.TryAttack(MeleeKey.A);
                    _modeRefusal = _bridge.LastRefusal;
                }

                return (_modeTried && !_animal.IsPlayingMode && !_bridge.IsAttacking && InStep > 0.3f) || InStep > 5f;
            }

            // ---- Hits -------------------------------------------------------------------------------

            /// <summary>
            /// Presses the melee key until <paramref name="count"/> strikes have been made at a target
            /// standing in front of the player (put back in front as each swing begins), counting hits.
            /// </summary>
            bool StepStrikes(string label, int count, Func<GameObject> spawn)
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    _tally = new StrikeTally { label = label, wanted = count, startStrikes = _bridge.Strikes };
                    _tallies.Add(_tally);
                    _target = spawn();
                    _dummy = _target.GetComponent<MeleeTestDummy>();
                    PlaceTarget();
                    _lastMeleePhase = MeleeNow;
                    return false;
                }

                var phase = MeleeNow;
                if (phase == MeleePhase.Anticipation && _lastMeleePhase != MeleePhase.Anticipation)
                    PlaceTarget();
                _lastMeleePhase = phase;

                int done = _bridge.Strikes - _tally.startStrikes;
                if (done < count && (phase == MeleePhase.None || phase == MeleePhase.Recovery))
                    _bridge.TryAttack(MeleeKey.A);

                if ((done >= count && phase == MeleePhase.None) || InStep > 45f)
                {
                    _tally.strikes = done;
                    for (int s = _tally.startStrikes + 1; s <= _tally.startStrikes + done; s++)
                    {
                        int n = _tally.hitsPerStrike.TryGetValue(s, out int hits) ? hits : 0;
                        if (n > 0)
                            _tally.struck++;
                        if (n > 1)
                            _tally.doubles++;
                    }

                    if (_dummy != null)
                    {
                        _tally.targetHits = _dummy.Hits;
                        _tally.targetDamage = _dummy.Total;
                    }

                    Object.Destroy(_target);
                    _target = null;
                    _dummy = null;
                    _tally = null;
                    return true;
                }

                return false;
            }

            void OnMeleeHit(MAnimalGC2Bridge.MeleeHit hit)
            {
                if (_tally == null)
                    return;
                _tally.hits++;
                if (hit.Critical)
                    _tally.criticals++;
                _tally.hitsPerStrike[hit.Strike] = (_tally.hitsPerStrike.TryGetValue(hit.Strike, out int n) ? n : 0) + 1;
            }

            void PlaceTarget()
            {
                if (_target == null)
                    return;
                var root = _animal.transform;
                var forward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
                _target.transform.SetPositionAndRotation(root.position + forward * TargetDistance,
                    Quaternion.LookRotation(-forward, Vector3.up));
            }

            GameObject Dummy(bool head, bool body, float headHeight) =>
                MeleeTestDummy.Create("Melee test dummy", _animal.transform.position, Quaternion.identity, body, head, headHeight).gameObject;

            GameObject Crate()
            {
                var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
                crate.name = "Melee test crate";
                crate.transform.localScale = new Vector3(0.6f, 1.6f, 0.6f);
                var pivot = new GameObject("Melee test crate (pivot)");
                crate.transform.SetParent(pivot.transform, false);
                crate.transform.localPosition = new Vector3(0f, 0.8f, 0f);
                return pivot;
            }

            // ---- A zombie ---------------------------------------------------------------------------

            /// <summary>Most strikes to give the zombie before calling it unkillable.</summary>
            const int ZombieStrikes = 15;

            bool _zombieTried, _zombieDead;
            int _zombieStrikes, _zombieHits, _zombieStartStrikes;
            float _zombieHealth = -1f, _zombieHealthLeft = -1f;
            string _zombieNote;
            ZombieHealth _zombie;

            /// <summary>
            /// The game's zombie (Resources/Zombie), standing still in front of the player with its
            /// hitboxes open to melee, struck until it dies. Its AI is switched off: there is no NavMesh
            /// here, and it would fight back.
            /// </summary>
            bool StepZombie()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    var prefab = Resources.Load<GameObject>("Zombie");
                    if (prefab == null)
                    {
                        _zombieNote = "no Resources/Zombie prefab";
                        return true;
                    }

                    _target = Object.Instantiate(prefab);
                    _target.name = "Melee test zombie";
                    foreach (var behaviour in _target.GetComponentsInChildren<Behaviour>(true))
                    {
                        if (behaviour == null)
                            continue; // a missing script
                        string type = behaviour.GetType().Name;
                        if (type == "ZombieEnemy" || type == "NavMeshAgent")
                            behaviour.enabled = false;
                    }

                    int opened = 0;
                    foreach (var collider in _target.GetComponentsInChildren<Collider>(true))
                    {
                        if (collider.isTrigger && collider.GetComponent<TeamCanHit>() == null)
                        {
                            collider.gameObject.AddComponent<TeamCanHit>();
                            opened++;
                        }
                    }

                    _zombie = _target.GetComponent<ZombieHealth>();
                    _zombieTried = _zombie != null;
                    _zombieHealth = _zombie != null ? _zombie.Health : -1f;
                    _zombieStartStrikes = _bridge.Strikes;
                    _zombieNote = $"{opened} hitboxes opened to melee";
                    _bridge.HitApplied += OnZombieHit;
                    PlaceTarget();
                    _lastMeleePhase = MeleeNow;
                    return !_zombieTried;
                }

                var phase = MeleeNow;
                if (phase == MeleePhase.Anticipation && _lastMeleePhase != MeleePhase.Anticipation && !_zombieDead)
                    PlaceTarget();
                _lastMeleePhase = phase;

                _zombieDead = _zombie == null || _zombie.IsDead;
                int done = _bridge.Strikes - _zombieStartStrikes;
                if (!_zombieDead && done < ZombieStrikes && (phase == MeleePhase.None || phase == MeleePhase.Recovery))
                    _bridge.TryAttack(MeleeKey.A);

                if ((_zombieDead || done >= ZombieStrikes) && phase == MeleePhase.None || InStep > 40f)
                {
                    _zombieStrikes = done;
                    _zombieHealthLeft = _zombie != null ? _zombie.Health : 0f;
                    _bridge.HitApplied -= OnZombieHit;
                    if (_target != null)
                        Object.Destroy(_target);
                    _target = null;
                    return true;
                }

                return false;
            }

            void OnZombieHit(MAnimalGC2Bridge.MeleeHit hit)
            {
                if (_zombie != null && hit.Enemy == _zombie)
                    _zombieHits++;
            }

            // ---- A teammate ---------------------------------------------------------------------------

            const int TeammateStrikes = 3;

            bool _mateTried, _mateReacted, _mateHurt;
            int _mateStrikes, _mateStartStrikes, _mateHits;
            string _mateNote;
            GameObject _mate;
            MAnimalGC2Bridge _mateBridge;

            /// <summary>
            /// Another spike player standing in front, struck three times: friendly fire is off, so it
            /// must take no hit, and Game Creator must play no hit reaction on it.
            /// </summary>
            bool StepTeammate()
            {
                if (!_stepStarted)
                {
                    if (!Standing())
                        return InStep > 5f;
                    _stepStarted = true;
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GC2MeleeSpikeBuilder.PlayerPath);
                    if (prefab == null)
                    {
                        _mateNote = "no spike player prefab to stand in as a teammate";
                        return true;
                    }

                    var root = _animal.transform;
                    var forward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
                    _mate = Object.Instantiate(prefab, root.position + forward * TargetDistance, Quaternion.LookRotation(-forward, Vector3.up));
                    _mate.name = "Melee test teammate";
                    _mateBridge = _mate.GetComponent<MAnimalGC2Bridge>();
                    _mateTried = true;
                    _mateStartStrikes = _bridge.Strikes;
                    _bridge.HitApplied += OnMateHit;
                    return false;
                }

                // Let it land and settle before swinging at it.
                if (InStep < 1f)
                    return false;

                if (_mateBridge != null && _mateBridge.Melee != null && _mateBridge.Melee.CurrentPhase == MeleePhase.Reaction)
                    _mateReacted = true;
                var mateAnimal = _mateBridge != null ? _mateBridge.Animal : null;
                if (mateAnimal != null && mateAnimal.IsPlayingMode)
                    _mateHurt = true;

                var phase = MeleeNow;
                int done = _bridge.Strikes - _mateStartStrikes;
                if (done < TeammateStrikes && (phase == MeleePhase.None || phase == MeleePhase.Recovery))
                    _bridge.TryAttack(MeleeKey.A);

                if ((done >= TeammateStrikes && phase == MeleePhase.None) || InStep > 20f)
                {
                    _mateStrikes = done;
                    _bridge.HitApplied -= OnMateHit;
                    if (_mate != null)
                        Object.Destroy(_mate);
                    _mate = null;
                    return true;
                }

                return false;
            }

            void OnMateHit(MAnimalGC2Bridge.MeleeHit hit)
            {
                if (_mate != null && hit.Struck != null && hit.Struck.transform.IsChildOf(_mate.transform))
                    _mateHits++;
            }

            // ---- Verdict and report ------------------------------------------------------------------

            void JudgeMelee()
            {
                if (_gc2 == null || _bridge == null || _sword == null)
                    return;

                if (!_lockStarted)
                    _failures.Add("Melee: the attack for the lock test was refused (" + _lockRefusal + ").");
                else
                {
                    if (!_lockLocked)
                        _failures.Add("Melee: Malbers' movement and input were not locked during the attack.");
                    if (_lockJumped)
                        _failures.Add("Melee: Jump pressed during the attack made the character jump.");
                    if (_lockMoved > 1f)
                        _failures.Add($"Melee: with the stick held the body moved {_lockMoved:0.00} m during the attack: the stick walked it.");
                }

                if (!_airTried)
                    _failures.Add("Melee: the air test never left the ground.");
                else
                {
                    if (!_airRefused)
                        _failures.Add("Melee: an attack was allowed in the air.");
                    if (_airStruck)
                        _failures.Add("Melee: an attack started in the air reached its strike.");
                }

                if (_liftStarted)
                {
                    if (_liftCancelledAt < 0f || _airborneAt < 0f)
                        _failures.Add("Melee: lifting the body off the ground mid-attack did not cancel the attack.");
                    else if (_liftCancelledAt - _liftedAt > CancelLimit)
                        _failures.Add($"Melee: the attack was cancelled {_liftCancelledAt - _liftedAt:0.00} s after the body left the ground (limit {CancelLimit:0.00} s).");
                    else if (_liftReason == null)
                        _failures.Add("Melee: the lifted attack ended, but not by the bridge's rules.");
                }

                if (_modeTried && !_modeRefused)
                    _failures.Add("Melee: an attack was allowed during Malbers' mode.");

                if (_minTimeScale < 0.999f || _maxTimeScale > 1.001f)
                    _failures.Add($"Melee: the time scale moved ({_minTimeScale:0.###} to {_maxTimeScale:0.###}); it must stay 1 in a co-op game.");

                if (_mateTried)
                {
                    if (_mateStrikes < TeammateStrikes)
                        _failures.Add($"Melee: only {_mateStrikes} of {TeammateStrikes} strikes were made at the teammate.");
                    if (_mateHits > 0)
                        _failures.Add($"Melee: the teammate took {_mateHits} hits (friendly fire must be off).");
                    if (_mateReacted)
                        _failures.Add("Melee: Game Creator played a hit reaction on the teammate.");
                    if (_mateHurt)
                        _failures.Add("Melee: a Malbers mode (a hit reaction) played on the teammate.");
                }

                if (_zombieTried && !_zombieDead)
                    _failures.Add($"Melee: the zombie survived {_zombieStrikes} strikes ({_zombieHits} hits, health {_zombieHealthLeft:0} of {_zombieHealth:0}).");

                foreach (var t in _tallies)
                {
                    if (t.strikes < t.wanted)
                        _failures.Add($"Melee: {t.label}: only {t.strikes} of {t.wanted} strikes were made.");
                    if (t.label.StartsWith("Plain prop", StringComparison.Ordinal))
                    {
                        if (t.hits > 0)
                            _failures.Add($"Melee: the plain prop took {t.hits} hits: props must never be hurt.");
                        continue;
                    }

                    bool headOnly = t.label.Contains("head only");
                    if (headOnly && t.struck == 0)
                        _failures.Add($"Melee: {t.label}: no strike hit it.");
                    else if (!headOnly && t.struck < t.strikes)
                        _failures.Add($"Melee: {t.label}: {t.struck} of {t.strikes} strikes hit it.");
                    if (t.doubles > 0)
                        _failures.Add($"Melee: {t.label}: {t.doubles} strikes hurt it twice (one damage per strike).");
                    if (t.targetHits != t.hits)
                        _failures.Add($"Melee: {t.label}: the bridge applied {t.hits} hits but the dummy took {t.targetHits}.");
                    if (t.label.Contains("head only") && t.criticals < t.hits)
                        _failures.Add($"Melee: {t.label}: {t.hits - t.criticals} of its {t.hits} hits were not critical.");
                }
            }

            void WriteMelee(StringBuilder md)
            {
                if (_gc2 == null || _bridge == null || (_sword == null && _swordNote == null))
                    return;

                md.AppendLine("## Melee rules and hits (Game Creator, team sword)");
                if (_swordNote != null)
                    md.AppendLine("- " + _swordNote);
                if (_minTimeScale <= _maxTimeScale)
                    md.AppendLine($"- Time scale through all of it: {_minTimeScale:0.###} to {_maxTimeScale:0.###}.");
                if (_lockStarted)
                    md.AppendLine($"- Attack with the stick held forward and Jump pressed: Malbers' movement and input " +
                                  $"{(_lockLocked ? "locked" : "**not locked**")}; body moved {_lockMoved:0.00} m during it " +
                                  $"(walking covers about 4 m in that time); {(_lockJumped ? "**jumped**" : "did not jump")}.");
                else
                    md.AppendLine("- Lock test: the attack was refused (" + _lockRefusal + ").");
                if (_airTried)
                    md.AppendLine($"- In the air: attack {(_airRefused ? "refused (" + _airRefusal + ")" : "**allowed**")}; one forced " +
                                  $"anyway was {(_airCancel != null ? "cancelled (" + _airCancel + ")" : "not cancelled")} and " +
                                  $"{(_airStruck ? "**reached its strike**" : "never struck")}.");
                if (_liftStarted)
                    md.AppendLine(_liftCancelledAt >= 0f && _airborneAt >= 0f
                        ? $"- Lifted 1.2 m off the ground mid-attack: cancelled {_liftCancelledAt - _liftedAt:0.000} s later ({_liftReason ?? "not by the bridge"}); Malbers reported leaving the ground {_airborneAt - _liftedAt:0.000} s after the lift."
                        : "- Lifted off the ground mid-attack: **not cancelled**.");
                if (_modeTried)
                    md.AppendLine($"- During Malbers' {_mode?.ID?.name} mode: attack {(_modeRefused ? "refused (" + _modeRefusal + ")" : "**allowed**")}.");
                else if (_modeNote != null)
                    md.AppendLine("- Mode test: " + _modeNote + ".");

                if (_zombieTried)
                    md.AppendLine($"- The game's zombie ({_zombieNote}): {(_zombieDead ? "killed" : "**survived**")} after {_zombieStrikes} strikes " +
                                  $"and {_zombieHits} hits, health {_zombieHealth:0} to {Mathf.Max(0f, _zombieHealthLeft):0}.");
                else if (_zombieNote != null)
                    md.AppendLine("- Zombie test: " + _zombieNote + ".");

                if (_mateTried)
                    md.AppendLine($"- A teammate (another spike player) struck {_mateStrikes} times: {_mateHits} hits, " +
                                  $"{(_mateReacted ? "**a Game Creator hit reaction**" : "no Game Creator hit reaction")}, " +
                                  $"{(_mateHurt ? "**a Malbers mode played**" : "no Malbers reaction")}.");
                else if (_mateNote != null)
                    md.AppendLine("- Teammate test: " + _mateNote + ".");

                if (_tallies.Count > 0)
                {
                    md.AppendLine();
                    md.AppendLine("A head alone is a 16 cm target: swings passing above or below it miss, which is the swing, not the hit test.");
                    md.AppendLine();
                    md.AppendLine("| Target | Strikes | Hit by | Hurt twice by one strike | Hits | Critical | Target took |");
                    md.AppendLine("|---|---|---|---|---|---|---|");
                    foreach (var t in _tallies)
                        md.AppendLine($"| {t.label} | {t.strikes} | {t.struck} | {t.doubles} | {t.hits} | {t.criticals} | " +
                                      (t.label.StartsWith("Plain prop", StringComparison.Ordinal) ? "-" : $"{t.targetHits} hits, {t.targetDamage:0} damage") + " |");
                }

                md.AppendLine();
            }
        }
    }
}
#endif
