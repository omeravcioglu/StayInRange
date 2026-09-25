using EmeraldAI;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Emerald's ally-death callback only removes the dead AI from lists.
    /// Witnesses who see or hear a kill get put into combat against the player.
    /// </summary>
    public class Scene2AllyCombatAlert : MonoBehaviour
    {
        const float SeeRadius = 35f;
        const float HearRadius = 16f;

        EmeraldHealth _health;

        void Awake()
        {
            _health = GetComponent<EmeraldHealth>();
            if (_health != null)
                _health.OnDeath += AlertNearby;
        }

        void OnDestroy()
        {
            if (_health != null)
                _health.OnDeath -= AlertNearby;
        }

        void AlertNearby()
        {
            var player = Scene2CombatBootstrap.FindExistingFpsPlayer();
            if (player == null)
                return;

            var deathPos = transform.position + Vector3.up;
            var playerPos = player.transform.position + Vector3.up;
            int block = LayerMask.GetMask("Default", "Ground");

            foreach (var ai in FindObjectsByType<EmeraldSystem>(FindObjectsSortMode.None))
            {
                if (ai == null || ai.gameObject == gameObject)
                    continue;
                if (ai.AnimationComponent != null && ai.AnimationComponent.IsDead)
                    continue;
                if (ai.CombatComponent != null && ai.CombatComponent.CombatState && ai.CombatTarget != null)
                    continue;

                float toDeath = Vector3.Distance(ai.transform.position, transform.position);
                if (toDeath > SeeRadius)
                    continue;

                var head = ai.DetectionComponent != null && ai.DetectionComponent.HeadTransform != null
                    ? ai.DetectionComponent.HeadTransform.position
                    : ai.transform.position + Vector3.up * 1.6f;

                bool heard = toDeath <= HearRadius;
                bool sawKill = !Physics.Linecast(head, deathPos, block, QueryTriggerInteraction.Ignore);
                bool sawPlayer = !Physics.Linecast(head, playerPos, block, QueryTriggerInteraction.Ignore);
                if (!heard && !sawKill && !sawPlayer)
                    continue;

                if (ai.DetectionComponent != null)
                    ai.DetectionComponent.SetDetectedTarget(player.transform);
            }
        }
    }
}
