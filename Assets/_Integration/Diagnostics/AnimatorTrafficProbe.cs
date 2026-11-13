using System.Collections.Generic;
using MalbersAnimations;
using UnityEngine;

namespace CollarCali.Diagnostics
{
    /// <summary>
    /// Counts what an Animator sends its character, for checks that something runs exactly once:
    /// the messages of Malbers' animation behaviours (MessagesBehavior, e.g. the jump's take-off cue),
    /// and the Animator's root-motion callbacks, inside and outside the physics step. A second copy
    /// of the Animator Controller - Game Creator's animation graph makes one - shows as messages
    /// arriving twice; an Animator moved off the physics step shows as callbacks outside it.
    ///
    /// Put it on the Animator's object before play: Malbers' behaviours look for listeners the first
    /// time they run. CharacterLiveCheck adds it to the player when the check starts. Costs nothing
    /// that matters, but it is a test aid: leave it off prefabs.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class AnimatorTrafficProbe : MonoBehaviour, IAnimatorListener
    {
        readonly Dictionary<string, int> _messages = new Dictionary<string, int>();
        readonly List<(float time, string message)> _recent = new List<(float, string)>();

        /// <summary>Messages received so far, by name.</summary>
        public IReadOnlyDictionary<string, int> Messages => _messages;

        /// <summary>The latest messages with the time they arrived (Time.time), oldest first.</summary>
        public IReadOnlyList<(float time, string message)> Recent => _recent;

        /// <summary>Root-motion callbacks so far, and how many came inside a physics step.</summary>
        public int AnimatorMoves { get; private set; }
        public int AnimatorMovesInPhysics { get; private set; }

        /// <summary>Physics steps so far, and the most root-motion callbacks seen in one.</summary>
        public int PhysicsSteps { get; private set; }
        public int MostMovesInOneStep { get; private set; }

        int _movesThisStep;

        public int Count(string message) => _messages.TryGetValue(message, out int n) ? n : 0;

        public bool OnAnimatorBehaviourMessage(string message, object value)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            _messages[message] = Count(message) + 1;
            _recent.Add((Time.time, message));
            if (_recent.Count > 256)
                _recent.RemoveAt(0);
            return false; // a listener's answer only feeds Malbers' debug log
        }

        void FixedUpdate()
        {
            PhysicsSteps++;
            _movesThisStep = 0;
        }

        void OnAnimatorMove()
        {
            AnimatorMoves++;
            if (Time.inFixedTimeStep)
            {
                AnimatorMovesInPhysics++;
                _movesThisStep++;
                if (_movesThisStep > MostMovesInOneStep)
                    MostMovesInOneStep = _movesThisStep;
            }
        }
    }
}
