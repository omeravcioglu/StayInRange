using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator 2 driver for a character Malbers moves (see MAnimalGC2Bridge). It reports
    /// Malbers' grounding, floor and velocity, and hands Game Creator's moves - a melee lunge, a warp
    /// to a target, a turn - to Malbers through the bridge. It never moves the transform, adds no
    /// components and keeps no physics of its own.
    /// </summary>
    [Title("Malbers Animal (passive)")]
    [Category("Malbers Animal (passive)")]
    [Description("Reports Malbers' grounding and velocity; hands Game Creator's moves to Malbers")]
    [Serializable]
    public class UnitDriverMAnimal : TUnitDriver
    {
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [NonSerialized] private MAnimalGC2Bridge m_Bridge;

        MAnimalGC2Bridge Bridge
        {
            get
            {
                if (m_Bridge == null)
                    m_Bridge = MAnimalGC2Bridge.Of(Character);
                return m_Bridge;
            }
        }

        MAnimal Animal => Bridge != null ? Bridge.Animal : null;

        public override Vector3 WorldMoveDirection
        {
            get
            {
                var animal = Animal;
                return animal != null && animal.RB != null ? animal.RB.linearVelocity : Vector3.zero;
            }
        }

        public override Vector3 LocalMoveDirection =>
            Transform != null ? Transform.InverseTransformDirection(WorldMoveDirection) : Vector3.zero;

        public override float SkinWidth => 0f;

        public override bool IsGrounded
        {
            get
            {
                var animal = Animal;
                return m_ForceGrounded || (animal != null && animal.Grounded);
            }
        }

        public override Vector3 FloorNormal
        {
            get
            {
                var animal = Animal;
                return animal != null ? animal.SurfaceNormal : Vector3.up;
            }
        }

        /// <summary>Malbers keeps its own colliders; Game Creator cannot switch them off from here.</summary>
        public override bool Collision
        {
            get => true;
            set { }
        }

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            var bridge = Bridge;
            if (bridge != null)
                bridge.SetPosition(position, teleport);
        }

        public override void SetRotation(Quaternion rotation)
        {
            var bridge = Bridge;
            if (bridge != null)
                bridge.SetRotation(rotation);
        }

        public override void AddPosition(Vector3 amount)
        {
            var bridge = Bridge;
            if (bridge != null)
                bridge.AddPosition(amount);
        }

        public override void AddRotation(Quaternion amount)
        {
            var bridge = Bridge;
            if (bridge != null)
                bridge.AddRotation(amount);
        }

        /// <summary>The body keeps its size: Malbers' rays and capsules are fitted to it.</summary>
        public override void SetScale(Vector3 scale) { }

        public override void AddScale(Vector3 scale) { }

        public override void ResetVerticalVelocity()
        {
            var animal = Animal;
            if (animal != null)
                animal.ResetGravityValues();
        }
    }
}
