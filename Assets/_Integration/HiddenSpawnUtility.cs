using System.Collections.Generic;
using cowsins;
using UnityEngine;
using UnityEngine.AI;

namespace CollarCali
{
    /// <summary>
    /// Shared "can anybody see this spot" logic for anything that has to appear unseen.
    ///
    /// Used by the zombie ambush spawner and the creep stalker. It lives on its own because both need
    /// the identical test and the identical caveats, and two copies of a rule this fiddly would drift
    /// apart the first time one of them was tuned.
    /// </summary>
    public static class HiddenSpawnUtility
    {
        /// <summary>
        /// Position and facing of one player, from whichever representation exists - the networked
        /// bridge in a session, Cowsins' own stats when a scene is being tested on its own.
        /// </summary>
        public readonly struct PlayerView
        {
            public readonly Vector3 Position;
            public readonly float Yaw;

            public PlayerView(Vector3 position, float yaw)
            {
                Position = position;
                Yaw = yaw;
            }
        }

        static int _defaultBlockers;

        /// <summary>
        /// Geometry that blocks sight: everything solid except the things that are not architecture.
        /// An allowlist of Default and Ground would miss walls built on Object, Metal or Wood, which
        /// in this project is most of them.
        /// </summary>
        public static int DefaultSightBlockers()
        {
            if (_defaultBlockers != 0)
                return _defaultBlockers;

            int transparent = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Water",
                "Weapons", "Player", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            _defaultBlockers = ~transparent;
            return _defaultBlockers;
        }

        public static List<PlayerView> CollectPlayers()
        {
            var players = new List<PlayerView>();

#if CMPSETUP_COMPLETE
            var bridges = Object.FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None);
            if (bridges != null && bridges.Length > 0)
            {
                foreach (var bridge in bridges)
                {
                    if (bridge == null || bridge.IsDead || bridge.Object == null || !bridge.Object.IsValid)
                        continue;
                    players.Add(new PlayerView(bridge.GetNetworkAnchorPosition(), bridge.GetGameplayYaw()));
                }
                return players;
            }
#endif

            foreach (var stats in Object.FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (stats == null || stats.IsDead || !stats.gameObject.activeInHierarchy)
                    continue;

                // Camera yaw where there is one: offline the body does not necessarily turn with the
                // look direction.
                var camera = stats.GetComponentInChildren<Camera>();
                float yaw = camera != null ? camera.transform.eulerAngles.y : stats.transform.eulerAngles.y;
                players.Add(new PlayerView(stats.transform.position, yaw));
            }

            return players;
        }

        /// <summary>
        /// True when no player could see <paramref name="point"/>.
        ///
        /// Sight is tested before facing because in a labyrinth a wall decides it almost every time,
        /// and a blocked view makes the direction someone is facing irrelevant.
        ///
        /// Facing uses the replicated yaw rather than a real camera frustum: a remote player's pitch
        /// never reaches the machine running this, so a true frustum test is only available for the
        /// local player. A generous horizontal cone is the honest approximation, and erring wide only
        /// ever delays a spawn rather than letting one be seen.
        /// </summary>
        public static bool IsHidden(Vector3 point, List<PlayerView> players, float fieldOfView,
            float minDistance, int blockers, float eyeHeight = 1.6f)
        {
            foreach (var player in players)
            {
                var eye = player.Position + Vector3.up * eyeHeight;

                if (Vector3.Distance(eye, point) < minDistance)
                    return false;

                if (Physics.Linecast(eye, point, blockers, QueryTriggerInteraction.Ignore))
                    continue;

                var toPoint = point - eye;
                toPoint.y = 0f;
                if (toPoint.sqrMagnitude < 0.01f)
                    return false;

                var forward = Quaternion.Euler(0f, player.Yaw, 0f) * Vector3.forward;
                if (Vector3.Angle(forward, toPoint) < fieldOfView * 0.5f)
                    return false;
            }

            return true;
        }

        public static bool AnyPlayerWithin(Vector3 point, List<PlayerView> players, float range)
        {
            foreach (var player in players)
            {
                if (Vector3.Distance(player.Position, point) <= range)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A NavMesh point inside the radius that is also hidden.
        ///
        /// The spawner itself being out of sight is not enough: a radius of any size easily reaches
        /// around a corner into somebody's view, so the chosen point is what gets tested.
        /// </summary>
        public static bool TryFindHiddenNavPoint(Vector3 center, float radius, List<PlayerView> players,
            float fieldOfView, float minDistance, int blockers, out Vector3 result, int attempts = 10)
        {
            result = center;

            for (int i = 0; i < attempts; i++)
            {
                var offset = Random.insideUnitCircle * radius;
                var candidate = center + new Vector3(offset.x, 0f, offset.y);

                if (!NavMesh.SamplePosition(candidate, out var hit, 4f, NavMesh.AllAreas))
                    continue;

                // Chest height, not feet: a body is visible over low cover its feet are hidden behind.
                if (!IsHidden(hit.position + Vector3.up * 1.1f, players, fieldOfView, minDistance, blockers))
                    continue;

                result = hit.position;
                return true;
            }

            return false;
        }

        /// <summary>True offline, and in a session only on the master client.</summary>
        public static bool OwnsSpawning()
        {
#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            if (runner == null || !runner.IsRunning)
                return true;
            return runner.IsSharedModeMasterClient;
#else
            return true;
#endif
        }

        /// <summary>
        /// Finds the spawner an incoming spawn RPC was meant for, by the same hierarchy-path hash
        /// NetworkWorldActor binds actors with.
        /// </summary>
        public static IHiddenSpawnReceiver FindReceiverByKey(int key)
        {
#if CMPSETUP_COMPLETE
            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour is IHiddenSpawnReceiver receiver &&
                    NetworkWorldActor.MakeKey(behaviour.transform) == key)
                    return receiver;
            }
#endif
            return null;
        }
    }

    /// <summary>
    /// A spawner that can be told to spawn by the master client.
    ///
    /// One RPC serves every kind of hidden spawner rather than one per enemy type; the variant int is
    /// interpreted by whichever spawner receives it.
    /// </summary>
    public interface IHiddenSpawnReceiver
    {
        void SpawnFromNetwork(int sequence, Vector3 position, float yaw, int variant);

        /// <summary>
        /// Removes a previously spawned creature on this machine.
        ///
        /// Needed because the usual cleanup - NetworkWorldActor despawning an actor once its source
        /// dies - never happens for something that cannot die. Without an explicit message the
        /// master's copy would go and every client would be left with a stalker standing there
        /// forever.
        /// </summary>
        void DespawnFromNetwork(int sequence);
    }
}
