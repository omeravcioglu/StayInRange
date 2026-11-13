using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// A melee skill's hit, as damage through the team's damage path (DamageRouting, IDamageTarget):
    /// the same way every other player attack hurts zombies, so the enemy owning the struck collider
    /// takes it and routes it to its owner's machine.
    ///
    /// It goes in a skill's On Hit instructions (MeleeContentBuilder puts it in the team's copies).
    /// Game Creator runs those once per struck collider; a swing crossing a zombie's head and body
    /// would hit it twice. On a player with MAnimalGC2Bridge the hit is handed to the bridge, which
    /// keeps one per enemy per strike - critical if any of its hitboxes was critical - and applies it
    /// at the end of the frame. Elsewhere it is applied at once.
    /// </summary>
    [Title("Deal Damage (team)")]
    [Category("CollarCali/Deal Damage")]
    [Description("Hurts the enemy the melee struck, once per strike, critical on a head hitbox")]
    [Parameter("Damage", "Damage of a normal hit")]
    [Parameter("Critical Multiplier", "How much more a head (critical) hitbox takes")]
    [Keywords("Melee", "Damage", "Hit", "Hurt", "Sword")]
    [Serializable]
    public class InstructionTeamDealDamage : Instruction
    {
        [SerializeField] private float m_Damage = 25f;
        [SerializeField] private float m_CriticalMultiplier = 2f;

        public InstructionTeamDealDamage() { }

        public InstructionTeamDealDamage(float damage, float criticalMultiplier)
        {
            m_Damage = damage;
            m_CriticalMultiplier = criticalMultiplier;
        }

        public float Damage => m_Damage;

        public override string Title => $"Deal {m_Damage:0.#} damage to the struck enemy";

        protected override Task Run(Args args)
        {
            var struck = args.Target;
            if (struck == null)
                return DefaultResult;

            var bridge = args.Self != null ? args.Self.GetComponentInParent<MAnimalGC2Bridge>() : null;
            if (bridge != null)
                bridge.QueueHit(struck, m_Damage, m_CriticalMultiplier);
            else
                DamageRouting.TryApply(struck, m_Damage * (DamageRouting.IsCritical(struck) ? m_CriticalMultiplier : 1f));
            return DefaultResult;
        }
    }
}
