using System;
using System.Collections.Generic;
using MalbersAnimations.Controller;
using MalbersAnimations.Scriptables;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fits Malbers' controller to the player's body from PlayerTuning > Body, and keeps it fitted.
    /// Lives on the character's MAnimal; the player controller adds it next to the jump tuning.
    ///
    /// SPEED. Malbers moves the player by its animations (root motion), and Unity scales a humanoid's
    /// root motion by the size of its body (Animator.humanScale). The 1.87 m player takes the same
    /// steps as the 2.48 m model the levels were built with, only shorter, so it would cover about a
    /// fifth less ground. The animator speed of every speed in the root-motion speed sets is
    /// multiplied by Level Body Scale / humanScale (and Extra Speed): the legs step quicker and the
    /// player covers the old ground in the old time, with no foot sliding. Wall runs, swimming and
    /// flying push the body directly and are left alone.
    ///
    /// Malbers keeps speed sets per character, so the change never reaches another character or a
    /// project file. The values are rewritten in place, because Malbers keeps a copy of the current
    /// speed that shares them, and put back when this is disabled.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterBodyCalibration : MonoBehaviour
    {
        struct Shipped
        {
            public bool UseConstant;
            public float Constant;
            public float Value;
        }

        MAnimal _animal;
        readonly Dictionary<FloatReference, Shipped> _shipped = new Dictionary<FloatReference, Shipped>();
        float _applied = float.NaN;
        string _appliedSets;

        /// <summary>How much faster the root-motion speeds play than Malbers ships them (1 = as shipped).</summary>
        public float SpeedFactor => float.IsNaN(_applied) ? 1f : _applied;

        void OnDisable()
        {
            Restore();
        }

        void Update()
        {
            if (_animal == null)
                _animal = GetComponent<MAnimal>();
            if (_animal == null || _animal.Anim == null || _animal.speedSets == null)
                return;

            var body = PlayerTuning.Active.body;
            float factor = TargetFactor(_animal.Anim, body);
            string sets = body.rootMotionSpeedSets != null ? string.Join("|", body.rootMotionSpeedSets) : string.Empty;
            if (!float.IsNaN(_applied) && Mathf.Abs(factor - _applied) < 0.0005f && sets == _appliedSets)
                return;

            Apply(factor, body.rootMotionSpeedSets);
            _appliedSets = sets;
        }

        /// <summary>
        /// The speed-up for <paramref name="animator"/>'s body: Level Body Scale / humanScale times
        /// Extra Speed, or Extra Speed alone when Keep Level Speed is off or the body isn't humanoid.
        /// </summary>
        public static float TargetFactor(Animator animator, PlayerTuning.BodySettings body)
        {
            float factor = body.extraSpeed;
            if (body.keepLevelSpeed && animator != null && animator.isHuman && animator.humanScale > 0.01f)
                factor *= body.levelBodyScale / animator.humanScale;
            return Mathf.Clamp(factor, 0.5f, 2f);
        }

        void Apply(float factor, string[] setNames)
        {
            Restore();
            foreach (var set in _animal.speedSets)
            {
                if (set?.Speeds == null || setNames == null || Array.IndexOf(setNames, set.name) < 0)
                    continue;

                // MSpeed is a struct, but its FloatReferences are shared objects: writing through the
                // copy changes the set and Malbers' current-speed copy alike.
                foreach (var speed in set.Speeds)
                {
                    var animatorSpeed = speed.animator;
                    if (animatorSpeed == null || _shipped.ContainsKey(animatorSpeed))
                        continue;

                    var shipped = new Shipped
                    {
                        UseConstant = animatorSpeed.UseConstant,
                        Constant = animatorSpeed.ConstantValue,
                        Value = animatorSpeed.Value,
                    };
                    _shipped[animatorSpeed] = shipped;
                    animatorSpeed.UseConstant = true;
                    animatorSpeed.ConstantValue = shipped.Value * factor;
                }
            }

            _applied = factor;
        }

        void Restore()
        {
            foreach (var pair in _shipped)
            {
                pair.Key.UseConstant = pair.Value.UseConstant;
                pair.Key.ConstantValue = pair.Value.Constant;
            }

            _shipped.Clear();
            _applied = float.NaN;
        }
    }
}
