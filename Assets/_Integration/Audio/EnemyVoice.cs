using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The ambient half of an enemy's audio: idle burbling, chase groans and footfalls.
    ///
    /// Everything here is decided and played LOCALLY on each machine, with nothing sent over the
    /// wire. That is the whole point of it. Enemy brains only run on the master client, so anything
    /// driven from the brain has to be relayed; but movement is already replicated through
    /// NetworkWorldActor's NetworkTransform, so every client can watch the body move and work out
    /// for itself whether the thing is shambling or standing still. A groan every few seconds times
    /// a horde would be a lot of needless RPCs for something nobody can tell is unsynchronised.
    ///
    /// One-shot events that must line up with an animation - a swing, a roar, the death - are NOT
    /// here. Those come from the brain through GameSfx.PlayShared.
    ///
    /// Attached from Awake by each brain, never from Start: a proxy's brain component is often
    /// already disabled by the network sweep before Start would have run.
    /// </summary>
    [DisallowMultipleComponent]
    public class EnemyVoice : MonoBehaviour
    {
        public enum Kind
        {
            ZombieWalker,
            ZombieCrawler,
            Magician,
            Creep,
        }

        [SerializeField] Kind kind = Kind.ZombieWalker;

        /// <summary>Above this speed the body counts as moving. Well under a shamble.</summary>
        const float MovingSpeed = 0.35f;

        /// <summary>Metres between footfalls, scaled per kind. Distance-based, so it tracks speed.</summary>
        const float WalkerStride = 1.15f;

        static readonly Vector2 IdleGap = new Vector2(4.5f, 9f);
        static readonly Vector2 ChaseGap = new Vector2(2.2f, 4.5f);

        Vector3 _lastPosition;
        float _speed;
        bool _moving;
        float _strideAccumulator;
        float _nextVocal;
        bool _dead;
        SfxLoop _moveLoop;

        /// <summary>Adds the component if it is missing, and (re)configures its kind.</summary>
        public static EnemyVoice Attach(GameObject host, Kind kind)
        {
            if (host == null)
                return null;

            var voice = host.GetComponent<EnemyVoice>();
            if (voice == null)
                voice = host.AddComponent<EnemyVoice>();
            voice.kind = kind;
            return voice;
        }

        /// <summary>
        /// Silences the ambient track. Called from every death path, including the remote ones, so a
        /// corpse does not keep groaning while it lingers.
        /// </summary>
        public void Silence()
        {
            _dead = true;
            _moveLoop.Stop();
        }

        void OnDisable()
        {
            // A corpse is destroyed rather than disabled, but a pooled or deactivated enemy would
            // otherwise leave its chase loop running with nothing driving it.
            _moveLoop.Stop();
        }

        void Awake()
        {
            _lastPosition = transform.position;
            ScheduleNextVocal(idle: true);
        }

        void Update()
        {
            if (_dead)
                return;

            TrackMovement();
            TickFootsteps();
            TickMoveLoop();
            TickVocals();
        }

        void TrackMovement()
        {
            var position = transform.position;
            var delta = position - _lastPosition;
            delta.y = 0f;
            _lastPosition = position;

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            // Smoothed: a replicated transform arrives in steps, and raw per-frame deltas made
            // remote enemies flicker between "moving" and "still" several times a second.
            float instant = delta.magnitude / dt;
            _speed = Mathf.Lerp(_speed, instant, 1f - Mathf.Exp(-8f * dt));
            _strideAccumulator += delta.magnitude;
        }

        /// <summary>
        /// Hysteresis, not a single threshold: a replicated transform delivers movement in bursts,
        /// and one shared cut-off had the creep's chase loop stuttering on and off several times a
        /// second while it ran in a straight line.
        /// </summary>
        bool IsMoving
        {
            get
            {
                if (_moving && _speed < MovingSpeed)
                    _moving = false;
                else if (!_moving && _speed > MovingSpeed * 2f)
                    _moving = true;
                return _moving;
            }
        }

        /// <summary>
        /// The creep's running sound is a loop rather than per-step one-shots, and it is decided
        /// locally from the replicated transform. The brain could not do this job: it only exists on
        /// the master, and the player being charged at is usually on the other machine.
        /// </summary>
        void TickMoveLoop()
        {
            if (kind != Kind.Creep)
                return;

            bool moving = IsMoving;
            if (moving && !_moveLoop.IsPlaying)
                _moveLoop = GameSfx.PlayLoop(SfxId.CreepChaseLoop, transform);
            else if (!moving && _moveLoop.IsPlaying)
                _moveLoop.Stop();
        }

        void TickFootsteps()
        {
            float stride = kind == Kind.ZombieCrawler ? WalkerStride * 0.7f
                : kind == Kind.Creep ? WalkerStride * 1.5f
                : WalkerStride;

            if (_strideAccumulator < stride)
                return;

            _strideAccumulator = 0f;

            // The magician never moves and the creep carries its own looped chase sound, so neither
            // of them wants per-step audio.
            switch (kind)
            {
                case Kind.ZombieWalker:
                    GameSfx.Play(SfxId.ZombieFootstep, transform);
                    break;
                case Kind.ZombieCrawler:
                    GameSfx.Play(SfxId.CrawlerScrape, transform);
                    break;
            }
        }

        void TickVocals()
        {
            if (Time.time < _nextVocal)
                return;

            bool moving = IsMoving;
            GameSfx.Play(VocalFor(moving), transform);
            ScheduleNextVocal(idle: !moving);
        }

        SfxId VocalFor(bool moving)
        {
            switch (kind)
            {
                case Kind.ZombieCrawler:
                    return SfxId.CrawlerIdleGroan;
                case Kind.Magician:
                    return SfxId.MagicianIdleChant;
                case Kind.Creep:
                    // The creep is silent until it is provoked; its roar and chase loop come from
                    // the brain. Nothing ambient, or it would give away a monster that is meant to
                    // be waiting quietly in the dark.
                    return SfxId.None;
                default:
                    return moving ? SfxId.ZombieChaseGroan : SfxId.ZombieIdleGroan;
            }
        }

        void ScheduleNextVocal(bool idle)
        {
            var gap = idle ? IdleGap : ChaseGap;
            _nextVocal = Time.time + Random.Range(gap.x, gap.y);
        }
    }
}
