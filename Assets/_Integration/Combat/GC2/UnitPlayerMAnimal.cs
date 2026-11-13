using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator 2 player input for a character Malbers moves (see MAnimalGC2Bridge). The keys
    /// stay Malbers' (and the team's); this only tells Game Creator which way the player is pushing,
    /// for melee skills that care (an attack while moving forward, a dodge to the side).
    /// </summary>
    [Title("Malbers Animal (passive)")]
    [Category("Malbers Animal (passive)")]
    [Description("Reports the direction the player pushes Malbers; reads no keys of its own")]
    [Serializable]
    public class UnitPlayerMAnimal : TUnitPlayer
    {
        [NonSerialized] private MAnimalGC2Bridge m_Bridge;

        public override void OnUpdate()
        {
            if (m_Bridge == null)
                m_Bridge = MAnimalGC2Bridge.Of(Character);
            var animal = m_Bridge != null ? m_Bridge.Animal : null;
            InputDirection = IsControllable && animal != null
                ? Vector3.ClampMagnitude(animal.Move_Direction, 1f)
                : Vector3.zero;
        }
    }
}
