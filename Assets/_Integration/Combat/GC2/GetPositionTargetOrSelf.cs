using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// The target's position, or the character's own when there is no target. Game Creator's
    /// Target Position falls back to the world origin, which made the sword's combos measure "distance
    /// to the target" from the middle of the map: with no enemy targeted, its light attacks (target
    /// within 5 m) only worked near the origin. MeleeContentBuilder puts this in the team combos, so
    /// with no target the light attacks are always in reach and the gap closers (target far away) are
    /// not chosen; with a target it is Game Creator's own.
    /// </summary>
    [Title("Target Position (or Self)")]
    [Category("Game Objects/Target Position (or Self)")]
    [Image(typeof(IconTarget), ColorTheme.Type.Green)]
    [Description("Returns the position of the targeted object, or of Self when there is no target")]
    [Serializable]
    public class GetPositionTargetOrSelf : PropertyTypeGetPosition
    {
        public override Vector3 Get(Args args)
        {
            if (args.Target != null)
                return args.Target.transform.position;
            return args.Self != null ? args.Self.transform.position : default;
        }

        public static PropertyGetPosition Create() => new PropertyGetPosition(new GetPositionTargetOrSelf());

        public override string String => "Target (or Self)";
    }
}
