using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator 2 facing for a character Malbers turns (see MAnimalGC2Bridge). It reports where
    /// the body faces and never turns it: a melee skill that wants the body turned does so through
    /// the driver, which hands the turn to Malbers.
    /// </summary>
    [Title("Malbers Animal (passive)")]
    [Category("Malbers Animal (passive)")]
    [Description("Reports where Malbers faces the body; never turns it")]
    [Serializable]
    public class UnitFacingMAnimal : TUnitFacing
    {
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
        }

        public override void OnUpdate()
        {
            if (Transform == null)
                return;
            m_FaceDirection = Transform.forward;
            m_PivotSpeed = 0f;
        }

        protected override Vector3 GetDefaultDirection() => Transform != null ? Transform.forward : Vector3.forward;
    }
}
