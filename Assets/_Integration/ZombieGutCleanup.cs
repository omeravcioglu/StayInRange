using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Sweeps up the physics chunks the RVFX gut effect throws.
    ///
    /// The pack's EffectController instantiates its gut prefabs unparented, with rigidbodies and
    /// no lifetime of their own - so left alone they accumulate on the floor forever, which a
    /// horde turns into hundreds of rigidbodies. This finds the ones spawned alongside this effect
    /// and retires them.
    /// </summary>
    public class ZombieGutCleanup : MonoBehaviour
    {
        [SerializeField] float lifetime = 12f;

        /// <summary>How far from the burst a chunk can be and still count as ours.</summary>
        [SerializeField] float claimRadius = 4f;

        /// <summary>Chunk prefabs are named Gut_01_URP and so on, cloned as "..._URP(Clone)".</summary>
        [SerializeField] string namePrefix = "Gut_";

        Vector3 _origin;
        bool _swept;

        void Start()
        {
            _origin = transform.position;
        }

        void LateUpdate()
        {
            // One frame late: the chunks are created in the effect's OnEnable, which runs before
            // this component's first LateUpdate.
            if (_swept)
                return;
            _swept = true;

            foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (body == null)
                    continue;

                var go = body.gameObject;
                if (!go.name.StartsWith(namePrefix))
                    continue;
                if ((go.transform.position - _origin).sqrMagnitude > claimRadius * claimRadius)
                    continue;

                // Scheduled on the chunk itself, so it still fires after this effect's own root
                // has been destroyed a few seconds from now.
                Destroy(go, lifetime);
            }
        }
    }
}
