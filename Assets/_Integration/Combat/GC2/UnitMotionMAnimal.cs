using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator 2 motion for a character Malbers moves (see MAnimalGC2Bridge). Malbers owns
    /// speed, turning and jumping, so this only reports: the move direction is Malbers' velocity, the
    /// height and radius are its main capsule's, and Game Creator's own jump is off. The motion
    /// requests Game Creator would turn into movement go nowhere, since the driver ignores them.
    /// </summary>
    [Title("Malbers Animal (passive)")]
    [Category("Malbers Animal (passive)")]
    [Description("Reports Malbers' movement to Game Creator; Malbers moves, turns and jumps")]
    [Serializable]
    public class UnitMotionMAnimal : UnitMotionController
    {
        [NonSerialized] private MAnimalGC2Bridge m_Bridge;

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            m_Bridge = MAnimalGC2Bridge.Of(character);
            CanJump = false;
            AirJumps = 0;

            // Game Creator measures the body from its centre; Malbers' capsule starts above the feet.
            if (m_Bridge != null && m_Bridge.Animal != null && m_Bridge.Animal.MainCollider is CapsuleCollider capsule)
            {
                float scale = Mathf.Abs(character.transform.lossyScale.y);
                Height = (capsule.center.y + capsule.height * 0.5f) * scale;
                Radius = capsule.radius * scale;
            }
        }

        public override void OnUpdate()
        {
            base.OnUpdate();

            if (m_Bridge == null)
                m_Bridge = MAnimalGC2Bridge.Of(Character);
            var animal = m_Bridge != null ? m_Bridge.Animal : null;
            MoveDirection = animal != null ? animal.HorizontalVelocity : Vector3.zero;
        }

        /// <summary>Jumps are Malbers' (ThirdPersonJumpTuning tunes them).</summary>
        public override void Jump(float force) { }
    }
}
