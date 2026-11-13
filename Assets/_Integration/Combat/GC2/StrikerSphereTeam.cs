using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator's sphere striker (a fist, a foot), finding trigger colliders as well: every enemy
    /// hitbox in the game is a trigger, and Game Creator's own shapes ignore them.
    /// </summary>
    [Title("Sphere (sees hitboxes)")]
    [Category("Sphere (sees hitboxes)")]
    [Image(typeof(IconSphereSolid), ColorTheme.Type.Green)]
    [Description("A sphere that finds the colliders it overlaps, trigger hitboxes included")]
    [Serializable]
    public class StrikerSphereTeam : TStrikerShape
    {
        [SerializeField] private Vector3 m_Position = Vector3.zero;
        [SerializeField] private float m_Radius = 0.5f;

        public StrikerSphereTeam() { }

        public StrikerSphereTeam(Vector3 position, float radius)
        {
            m_Position = position;
            m_Radius = radius;
        }

        protected override Vector3 Position => m_Position;
        protected override Vector3 Rotation => Vector3.zero;

        protected override int Cast(Vector3 position, Quaternion rotation, LayerMask layerMask) =>
            Physics.OverlapSphereNonAlloc(position, m_Radius, HITS, layerMask, QueryTriggerInteraction.Collide);

        protected override Vector3 GetPoint(GameObject hit, Vector3 position, Quaternion rotation) => position;

        protected override void DrawGizmos(Vector3 position, Quaternion rotation) =>
            GizmosExtension.Octahedron(position, rotation, m_Radius, 4);
    }
}
