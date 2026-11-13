using GameCreator.Runtime.Characters;
using MalbersAnimations.Controller;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace CollarCali.Combat
{
    /// <summary>
    /// Lets a Game Creator 2 Character (melee) live on a Malbers MAnimal without the two fighting
    /// over the body. Malbers stays in charge of movement, physics and the Animator; the Character's
    /// units are passive (UnitPlayerMAnimal, UnitMotionMAnimal, UnitDriverMAnimal, UnitFacingMAnimal,
    /// UnitAnimimMAnimal): they report Malbers' state to Game Creator and never move the transform.
    ///
    /// What Game Creator asks of the body - the lunges and warps of a melee skill - comes in through
    /// the driver unit and is handed to Malbers here, as extra movement inside Malbers' own movement
    /// step (its PostStateMovement hook), so it goes through the Rigidbody and the ground checks like
    /// any other movement and is never applied twice.
    ///
    /// Animation: Game Creator plays its gestures through its own animation graph, which by default
    /// wraps a second copy of the Animator Controller. On Malbers' character that copy ran beside the
    /// Animator's own and every Malbers animation behaviour fired twice (the jump's take-off cue came
    /// twice), and the graph's last layer crashed Unity 6.0 in the physics step. The Overlay wiring
    /// (the default; see GraphWiring) cuts both out: Malbers' controller runs once, natively, and Game
    /// Creator's graph only lays its gestures over it, weighed by the gesture playing.
    ///
    /// Melee rules (the attack locks, when an attack may start and what cancels one) and the damage of
    /// its hits are in MAnimalGC2Bridge.Melee.cs.
    ///
    /// Spike: third-person plan, phase 2.
    /// </summary>
    [DisallowMultipleComponent]
    public partial class MAnimalGC2Bridge : MonoBehaviour
    {
        /// <summary>
        /// How Game Creator's animation graph is wired to the Animator. Game Creator wraps the
        /// Animator Controller in its own graph and ends it in a layer mixer with an empty, IK-only
        /// clip; with Malbers' Animator updating in the physics step, Unity 6.0 crashed processing that
        /// mixer's root motion on the first step (Stock). NoIKPass drops that last mixer, so the graph
        /// ends at Game Creator's gesture layer. With it the Animator's own copy of the controller
        /// still runs beside Game Creator's, and every Malbers animation behaviour fires twice (the
        /// jump's take-off cue arrives twice); SingleController also takes the controller off the
        /// Animator once Game Creator has its copy, so only that copy runs.
        /// </summary>
        public enum GraphWiring
        {
            Stock,
            NoIKPass,
            SingleController,
            Overlay,
        }

        // Overlay: Game Creator's graph keeps only its gesture layer, and its output weighs as much as
        // the gesture playing, so the Animator's own controller (Malbers', running once) shows the
        // rest of the time.
        AnimationPlayableOutput _overlay;
        bool _isOverlay;

        [Tooltip("How Game Creator's animation graph is wired to the Animator (spike).")]
        [SerializeField] GraphWiring m_Wiring = GraphWiring.Overlay;

        public GraphWiring Wiring => m_Wiring;

        public MAnimal Animal { get; private set; }
        public Character Character { get; private set; }

        /// <summary>What the wiring did, for the checks.</summary>
        public string WiringReport { get; private set; } = "not wired yet";

        bool _wired;

        Vector3 _move;
        Quaternion _turn = Quaternion.identity;
        bool _hasFacing;
        Quaternion _facing;
        bool _hooked;

        /// <summary>The bridge on <paramref name="character"/>, or null.</summary>
        public static MAnimalGC2Bridge Of(Character character) =>
            character != null ? character.GetComponent<MAnimalGC2Bridge>() : null;

        void Awake() => Resolve();

        void OnEnable()
        {
            Resolve();
            if (Animal != null && !_hooked)
            {
                Animal.PostStateMovement += ApplyMoves;
                _hooked = true;
            }

            // The Character builds its graph in its Awake, which runs earlier; the Animator first
            // evaluates it in the first physics step, after this.
            if (!_wired)
                Wire();
        }

        void Wire()
        {
            _wired = true;
            if (Character == null)
            {
                WiringReport = "no Game Creator Character";
                return;
            }

            var graph = Character.AnimationGraph;
            if (!graph.IsValid())
            {
                WiringReport = "Game Creator has no animation graph";
                return;
            }

            if (m_Wiring == GraphWiring.Stock)
            {
                WiringReport = "stock";
                return;
            }

            int count = graph.GetOutputCountByType<AnimationPlayableOutput>();
            for (int i = 0; i < count; i++)
            {
                var output = (AnimationPlayableOutput)graph.GetOutputByType<AnimationPlayableOutput>(i);
                var last = output.GetSourcePlayable();
                if (!last.IsValid() || last.GetPlayableType() != typeof(AnimationLayerMixerPlayable) || last.GetInputCount() < 1)
                    continue;

                var gestures = last.GetInput(0);
                var ikPass = last.GetInputCount() > 1 ? last.GetInput(1) : Playable.Null;
                graph.Disconnect(last, 0);
                if (ikPass.IsValid())
                {
                    graph.Disconnect(last, 1);
                    ikPass.Destroy();
                }

                last.Destroy();
                output.SetSourcePlayable(gestures);
                WiringReport = "IK pass removed: the graph ends at the gesture layer";
                break;
            }

            if (WiringReport != "IK pass removed: the graph ends at the gesture layer")
                WiringReport = "no IK pass layer found";

            if (m_Wiring == GraphWiring.SingleController && Animal != null && Animal.Anim != null)
            {
                Animal.Anim.runtimeAnimatorController = null;
                WiringReport += "; the Animator's own controller removed, Game Creator's copy runs alone";
            }

            if (m_Wiring == GraphWiring.Overlay)
                MakeOverlay(graph);
        }

        /// <summary>Cuts Game Creator's copy of the controller out of its graph and weighs its output by the gestures.</summary>
        void MakeOverlay(PlayableGraph graph)
        {
            int count = graph.GetOutputCountByType<AnimationPlayableOutput>();
            if (count == 0)
                return;

            _overlay = (AnimationPlayableOutput)graph.GetOutputByType<AnimationPlayableOutput>(0);
            var gestures = _overlay.GetSourcePlayable();        // Game Creator's gesture layer
            var states = gestures.IsValid() && gestures.GetInputCount() > 0 ? gestures.GetInput(0) : Playable.Null;
            var copy = states.IsValid() && states.GetInputCount() > 0 ? states.GetInput(0) : Playable.Null;
            if (copy.IsValid() && copy.GetPlayableType() == typeof(AnimatorControllerPlayable))
            {
                graph.Disconnect(states, 0);
                copy.Destroy();
                WiringReport += "; Game Creator's copy of the controller removed";
            }
            else
            {
                WiringReport += "; no controller copy found to remove";
            }

            _isOverlay = true;
            _overlayWeight = 0f;
            _overlay.SetWeight(0f);
            WiringReport += "; its output weighs as much as the playing gesture";
        }

        float _overlayWeight;

        /// <summary>How much Game Creator's gestures show over Malbers' pose right now (Overlay wiring).</summary>
        public float OverlayWeight => _isOverlay ? _overlayWeight : 1f;

        void UpdateOverlay()
        {
            if (!_isOverlay || !_overlay.IsOutputValid() || Character == null)
                return;

            var gestures = Character.Gestures;
            _overlayWeight = gestures != null && gestures.IsPlaying ? gestures.CurrentWeight : 0f;
            _overlay.SetWeight(_overlayWeight);
        }

        bool AnimatesInPhysics => Animal != null && Animal.Anim != null && Animal.Anim.updateMode == AnimatorUpdateMode.Fixed;

        // Just before the Animator evaluates: in the physics step (Malbers' update mode) or in the frame.
        void FixedUpdate()
        {
            if (AnimatesInPhysics)
                UpdateOverlay();
        }

        void Update()
        {
            if (!AnimatesInPhysics)
                UpdateOverlay();
        }

        void OnDisable()
        {
            if (Animal != null && _hooked)
                Animal.PostStateMovement -= ApplyMoves;
            _hooked = false;
            Clear();
            ReleaseMelee();
        }

        void LateUpdate()
        {
            // Game Creator's Character updates its melee in its LateUpdate, which runs earlier.
            TrackMelee();
            ApplyHits();
        }

        void Resolve()
        {
            if (Animal == null)
                Animal = GetComponent<MAnimal>();
            if (Character == null)
                Character = GetComponent<Character>();
        }

        /// <summary>Moves the body by <paramref name="amount"/> (world space) in Malbers' next movement step.</summary>
        public void AddPosition(Vector3 amount)
        {
            if (IsFinite(amount))
                _move += amount;
        }

        /// <summary>
        /// Puts the body at <paramref name="position"/>: at once when teleporting, otherwise by moving
        /// it there in Malbers' next movement step.
        /// </summary>
        public void SetPosition(Vector3 position, bool teleport)
        {
            if (!IsFinite(position))
                return;

            if (teleport && Animal != null)
            {
                Animal.Teleport(position);
                _move = Vector3.zero;
                return;
            }

            _move = position - transform.position;
        }

        /// <summary>Turns the body by <paramref name="amount"/> (world space) in Malbers' next movement step.</summary>
        public void AddRotation(Quaternion amount) => _turn = amount * _turn;

        /// <summary>Turns the body to face where <paramref name="rotation"/> faces, about its up axis.</summary>
        public void SetRotation(Quaternion rotation)
        {
            _facing = rotation;
            _hasFacing = true;
        }

        void ApplyMoves(MAnimal animal)
        {
            if (_move != Vector3.zero)
                animal.AdditivePosition += _move;

            var current = animal.transform.rotation;
            var up = animal.UpVector;
            var turn = _turn;
            if (_hasFacing)
            {
                var forward = Vector3.ProjectOnPlane(_facing * Vector3.forward, up);
                if (forward.sqrMagnitude > 1e-6f)
                {
                    var now = Vector3.ProjectOnPlane(current * Vector3.forward, up);
                    if (now.sqrMagnitude > 1e-6f)
                        turn = Quaternion.FromToRotation(now, forward) * turn;
                }
            }

            // Malbers applies its additive rotation in the body's own space (Rotation *= Additive).
            if (Quaternion.Angle(turn, Quaternion.identity) > 0.01f)
                animal.AdditiveRotation *= Quaternion.Inverse(current) * turn * current;

            Clear();
        }

        void Clear()
        {
            _move = Vector3.zero;
            _turn = Quaternion.identity;
            _hasFacing = false;
        }

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
