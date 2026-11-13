using System.Collections.Generic;
using CollarCali.Combat;
using UnityEngine;

namespace CollarCali.Diagnostics
{
    /// <summary>
    /// A stand-in enemy for melee checks, built like a zombie's hitboxes: trigger colliders on the
    /// Enemy layer - a body tagged BodyShot and a head tagged Critical - each open to Game Creator's
    /// melee (TeamCanHit), over a health that never dies and only counts what it takes.
    /// CharacterLiveCheck makes them; leave them out of scenes.
    /// </summary>
    [AddComponentMenu("")]
    public class MeleeTestDummy : MonoBehaviour, IDamageTarget
    {
        public int Hits { get; private set; }
        public int Criticals { get; private set; }
        public float Total { get; private set; }

        /// <summary>Every hit taken: when (Time.time), how much, and whether it was critical.</summary>
        public readonly List<(float time, float damage, bool critical)> Taken = new List<(float, float, bool)>();

        public void Damage(float damage, bool isHeadshot)
        {
            Hits++;
            if (isHeadshot)
                Criticals++;
            Total += damage;
            Taken.Add((Time.time, damage, isHeadshot));
        }

        public void Clear()
        {
            Hits = Criticals = 0;
            Total = 0f;
            Taken.Clear();
        }

        /// <summary>
        /// A dummy standing at <paramref name="position"/>: a 1.3 m body from 0.2 m up, and a head
        /// centred <paramref name="headHeight"/> up. Either can be left out.
        /// </summary>
        public static MeleeTestDummy Create(string name, Vector3 position, Quaternion rotation,
            bool body = true, bool head = true, float headHeight = 1.62f)
        {
            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(position, rotation);
            var dummy = root.AddComponent<MeleeTestDummy>();
            int layer = LayerMask.NameToLayer("Enemy");

            if (body)
            {
                var hitbox = Hitbox(root.transform, "HB_Body", "BodyShot", layer);
                var capsule = hitbox.AddComponent<CapsuleCollider>();
                capsule.isTrigger = true;
                capsule.radius = 0.25f;
                capsule.height = 1.3f;
                capsule.center = new Vector3(0f, 0.85f, 0f);
            }

            if (head)
            {
                var hitbox = Hitbox(root.transform, "HB_Head", "Critical", layer);
                var sphere = hitbox.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = 0.16f;
                sphere.center = new Vector3(0f, headHeight, 0f);
            }

            return dummy;
        }

        static GameObject Hitbox(Transform root, string name, string tag, int layer)
        {
            var hitbox = new GameObject(name);
            hitbox.transform.SetParent(root, false);
            hitbox.layer = layer >= 0 ? layer : 0;
            hitbox.tag = tag;
            hitbox.AddComponent<TeamCanHit>();
            return hitbox;
        }
    }
}
