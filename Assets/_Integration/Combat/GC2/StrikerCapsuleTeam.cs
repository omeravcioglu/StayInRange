using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator's capsule striker (the sword's blade), finding trigger colliders as well. Every
    /// enemy hitbox in the game is a trigger, and Game Creator's own shapes ignore triggers, so a
    /// stock sword passes through zombies. Same numbers as Game Creator's StrikerCapsule.
    /// </summary>
    [Title("Capsule (sees hitboxes)")]
    [Category("Capsule (sees hitboxes)")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Green)]
    [Description("A capsule that finds the colliders it overlaps, trigger hitboxes included")]
    [Serializable]
    public class StrikerCapsuleTeam : TStrikerShape
    {
        public enum Direction
        {
            X,
            Y,
            Z
        }

        [SerializeField] private Vector3 m_Position = Vector3.zero;
        [SerializeField] private float m_Height = 1f;
        [SerializeField] private float m_Radius = 0.1f;
        [SerializeField] private Direction m_Direction = Direction.X;

        public StrikerCapsuleTeam() { }

        public StrikerCapsuleTeam(Vector3 position, float height, float radius, Direction direction)
        {
            m_Position = position;
            m_Height = height;
            m_Radius = radius;
            m_Direction = direction;
        }

        protected override Vector3 Position => m_Position;
        protected override Vector3 Rotation => Vector3.zero;

        Vector3 HalfLength(Quaternion rotation) => rotation * (m_Direction switch
        {
            Direction.X => Vector3.right,
            Direction.Y => Vector3.up,
            _ => Vector3.forward,
        }) * (m_Height * 0.5f);

        protected override int Cast(Vector3 position, Quaternion rotation, LayerMask layerMask)
        {
            var half = HalfLength(rotation);
            return Physics.OverlapCapsuleNonAlloc(position + half, position - half, m_Radius, HITS,
                layerMask, QueryTriggerInteraction.Collide);
        }

        protected override Vector3 GetPoint(GameObject hit, Vector3 position, Quaternion rotation)
        {
            var half = HalfLength(rotation);
            return hit.transform.position.OnSegment(position + half, position - half);
        }

        protected override void DrawGizmos(Vector3 position, Quaternion rotation)
        {
            GizmosExtension.Capsule(position, rotation, m_Radius, m_Height, 24, m_Direction switch
            {
                Direction.X => (int)GizmosExtension.CapsuleDirection.AxisX,
                Direction.Y => (int)GizmosExtension.CapsuleDirection.AxisY,
                _ => (int)GizmosExtension.CapsuleDirection.AxisZ,
            });
        }
    }
}
