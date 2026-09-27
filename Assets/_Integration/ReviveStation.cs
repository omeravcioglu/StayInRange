#if CMPSETUP_COMPLETE
using System.Collections.Generic;
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
    /// can walk up still holding their teammate and revive them on the spot: while a body is held,
    /// the carrier's telekinesis turns its drop key into a revive here (see FindFor).
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

        // Every enabled station, so the carrier's per-frame check never scans the scene.
        static readonly List<ReviveStation> All = new List<ReviveStation>();

        /// <summary>Every enabled station, for the HUD (the spectator's "18 m to go").</summary>
        public static IReadOnlyList<ReviveStation> Active => All;

        /// <summary>How close a body has to be, in metres.</summary>
        public float BodyRange => bodyRange;

        void Reset()
        {
            interactText = "Revive teammate";
        }

        void OnEnable()
        {
            All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
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

        /// <summary>A dead teammate's body is close enough to revive.</summary>
        public bool HasBodyInRange => _candidate != null;

        /// <summary>Seconds until the station can revive again; 0 when ready.</summary>
        public float CooldownLeft => Mathf.Max(0f, _readyAt - Time.time);

        /// <summary>
        /// What the HUD prompt says for this station, in every state - including the blocked ones,
        /// for which Cowsins never calls <see cref="Highlight"/> and would show "Not Compatible".
        /// </summary>
        public string PromptText
        {
            get
            {
                if (CooldownLeft > 0f)
                    return "Recharging";
                return _candidate != null ? "Revive " + _candidate.DisplayName : "Revive";
            }
        }

        public override void Highlight()
        {
            base.Highlight();
            // DisplayName, like the carrier's own prompt. The input authority this used to read is
            // None for every player in shared mode, so it never named anybody.
            interactText = _candidate != null
                ? "Revive " + _candidate.DisplayName
                : "Bring a body here to revive";
        }

        /// <summary>Greys the prompt out when there is nothing here to revive.</summary>
        public override bool IsForbiddenInteraction(IWeaponReferenceProvider weaponController)
        {
            return _candidate == null || Time.time < _readyAt;
        }

        public override void Interact(Transform player)
        {
            TryRevive(_candidate, player);
        }

        /// <summary>
        /// Revives a dead player whose body is here. The station's own prompt calls this, and so
        /// does the carrier's telekinesis, so a body still held in the air comes back without being
        /// put down first.
        /// </summary>
        public bool TryRevive(FpsNetworkBridge body, Transform player)
        {
            if (body == null || !body.IsDead || Time.time < _readyAt)
                return false;

            _readyAt = Time.time + cooldownSeconds;

            // Sent to the dead player's own authority, which revives only if they are still dead.
            // Two survivors pressing at the same moment therefore produce one revive, not two.
            body.RequestRevive(SpawnPosition(), SpawnYaw());

            base.Interact(player);

            // A station is used many times over a level, so the one-shot latch the base class sets
            // is cleared again straight away.
            alreadyInteracted = false;
            return true;
        }

        /// <summary>
        /// The station a body can be revived at right now - the nearest one in range that is not
        /// cooling down - or null. Asked every frame by whoever is holding the body.
        /// </summary>
        public static ReviveStation FindFor(FpsNetworkBridge body)
        {
            if (body == null || !body.IsDead)
                return null;

            // The body itself rather than its root: a held body is simulated on the carrier's
            // machine, where the root trails it by a round trip to the owner and back.
            var position = body.DownState != null ? body.DownState.BodyPosition : body.transform.position;

            ReviveStation best = null;
            float bestDistance = float.MaxValue;

            foreach (var station in All)
            {
                if (station == null || Time.time < station._readyAt)
                    continue;

                float distance = Vector3.Distance(station.transform.position, position);
                if (distance > station.bodyRange || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = station;
            }

            return best;
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
