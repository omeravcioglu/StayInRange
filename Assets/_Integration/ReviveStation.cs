#if CMPSETUP_COMPLETE
using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Place one of these anywhere in a level and a survivor can bring a dead teammate's body to it
    /// and bring them back.
    ///
    /// Deliberately NOT a NetworkObject. The revive is a change to the DEAD player's state, and that
    /// player already has a networked object of their own, so the request is routed there. That
    /// means you can drop this prefab into any scene without registering anything with Fusion, and
    /// there is no station state that could get out of step between machines.
    ///
    /// It looks for a body rather than requiring one to be dropped in a precise spot, so a carrier
    /// can walk up still holding their teammate and revive them on the spot.
    /// </summary>
    [AddComponentMenu("CollarCali/Revive Station")]
    public class ReviveStation : Interactable
    {
        [Header("Revive")]
        [Tooltip("How close a dead body has to be, in metres.")]
        [SerializeField] float bodyRange = 3.2f;

        [Tooltip("Where the revived player stands up. Leave empty to use this object plus the offset below.")]
        [SerializeField] Transform spawnPoint;

        [Tooltip("Used when there is no spawn point, relative to this object.")]
        [SerializeField] Vector3 spawnOffset = new Vector3(0f, 0f, 1.1f);

        [Tooltip("Seconds before this station can be used again. Stops a double press reviving twice.")]
        [SerializeField] float cooldownSeconds = 2f;

        [Header("Feedback")]
        [Tooltip("Optional object switched on while a body is in range, so the station reads as ready.")]
        [SerializeField] GameObject readyIndicator;

        float _readyAt;
        float _nextScan;
        FpsNetworkBridge _candidate;

        void Reset()
        {
            interactText = "Revive teammate";
        }

        void Update()
        {
            // Scanned a few times a second rather than every frame: a level can hold a lot of these,
            // and a body does not arrive faster than this.
            if (Time.time < _nextScan)
                return;
            _nextScan = Time.time + 0.25f;

            _candidate = FindBodyInRange();

            if (readyIndicator != null)
            {
                bool ready = _candidate != null;
                if (readyIndicator.activeSelf != ready)
                    readyIndicator.SetActive(ready);
            }
        }

        public override void Highlight()
        {
            base.Highlight();
            interactText = _candidate != null
                ? "Revive player " + _candidate.Object.InputAuthority.PlayerId
                : "Bring a body here to revive";
        }

        /// <summary>Greys the prompt out when there is nothing here to revive.</summary>
        public override bool IsForbiddenInteraction(IWeaponReferenceProvider weaponController)
        {
            return _candidate == null || Time.time < _readyAt;
        }

        public override void Interact(Transform player)
        {
            var target = _candidate;
            if (target == null || Time.time < _readyAt)
                return;

            _readyAt = Time.time + cooldownSeconds;

            // Sent to the dead player's own authority, which revives only if they are still dead.
            // Two survivors pressing at the same moment therefore produce one revive, not two.
            target.RequestRevive(SpawnPosition(), SpawnYaw());

            base.Interact(player);

            // A station is used many times over a level, so the one-shot latch the base class sets
            // is cleared again straight away.
            alreadyInteracted = false;
        }

        Vector3 SpawnPosition()
        {
            return spawnPoint != null ? spawnPoint.position : transform.TransformPoint(spawnOffset);
        }

        float SpawnYaw()
        {
            return spawnPoint != null ? spawnPoint.eulerAngles.y : transform.eulerAngles.y;
        }

        /// <summary>
        /// The nearest dead player whose body is here. A dead player's network root follows their
        /// ragdoll, so its position is the body's position - carried or lying on the floor.
        /// </summary>
        FpsNetworkBridge FindBodyInRange()
        {
            FpsNetworkBridge best = null;
            float bestDistance = float.MaxValue;
            var here = transform.position;

            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (bridge == null || !bridge.IsDead || bridge.Object == null || !bridge.Object.IsValid)
                    continue;

                float distance = Vector3.Distance(here, bridge.transform.position);
                if (distance > bodyRange || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = bridge;
            }

            return best;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.95f, 0.5f, 0.85f);
            Gizmos.DrawWireSphere(transform.position, bodyRange);
            Gizmos.color = new Color(1f, 0.9f, 0.3f, 0.95f);
            var spawn = Application.isPlaying ? SpawnPosition() : transform.TransformPoint(spawnOffset);
            Gizmos.DrawWireSphere(spawnPoint != null ? spawnPoint.position : spawn, 0.3f);
        }
    }
}
#endif
