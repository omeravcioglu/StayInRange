using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Spawns and drives the magician's magic bolt.
    ///
    /// The bolt is not a Fusion NetworkObject - spawning one per shot would be heavy. Instead the
    /// master fires an authoritative bolt (this one raycasts and deals damage) and broadcasts the
    /// same origin/direction/speed to every other client, which each fly a cosmetic copy along the
    /// identical straight line. Deterministic flight means the impact lands in the same place on
    /// every screen; only the master's copy actually hurts anyone. Same pattern as the blood relay.
    /// </summary>
    public static class MagicProjectileFx
    {
        const string ProjectilePrefabName = "MagicianProjectile";
        const string MuzzlePrefabName = "MagicianMuzzle";
        const string HitPrefabName = "MagicianHit";

        const float MaxLifetime = 6f;

        /// <summary>
        /// Sweep radius for the world check.
        ///
        /// Was 0.35, which is what made bolts detonate in mid-air before reaching anything: a sphere
        /// that wide clips a wall, floor or railing from 35 cm away, and these are fired downwards
        /// from a perch along a corridor, so the sweep grazed the floor within the first few frames
        /// almost every time. Small enough now to pass down a corridor, still fat enough not to slip
        /// through a thin railing.
        /// </summary>
        const float HitRadius = 0.12f;

        /// <summary>
        /// Distance the bolt ignores world geometry for after spawning.
        ///
        /// It leaves the caster's raised hand, which sits inside the magician's own silhouette and
        /// often right beside the perch it is standing on. Without this the bolt can pop against the
        /// thing that fired it. Kept short so a bolt fired point-blank into a wall still detonates on
        /// that wall rather than sailing through it.
        /// </summary>
        const float SpawnGraceDistance = 0.8f;

        static GameObject _projectile, _muzzle, _hit;
        static bool _resolved;
        static int _hitMask = -1;

        static void EnsureResolved()
        {
            if (_resolved)
                return;
            _resolved = true;
            _projectile = Load(ProjectilePrefabName);
            _muzzle = Load(MuzzlePrefabName);
            _hit = Load(HitPrefabName);
        }

        /// <summary>
        /// Master entry point: spawn the damaging bolt here and tell everyone else to draw a copy.
        /// </summary>
        public static void Fire(Vector3 origin, Vector3 direction, float speed, float damage, GameObject owner)
        {
            SpawnMuzzle(origin, direction);
            SpawnBolt(origin, direction, speed, damage, owner, authoritative: true);

#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay != null)
                relay.BroadcastProjectile(origin, direction, speed);
#endif
        }

        /// <summary>Cosmetic-only copy, spawned on remote clients from the relay RPC.</summary>
        public static void SpawnVisual(Vector3 origin, Vector3 direction, float speed)
        {
            SpawnMuzzle(origin, direction);
            SpawnBolt(origin, direction, speed, 0f, null, authoritative: false);
        }

        static void SpawnBolt(Vector3 origin, Vector3 direction, float speed, float damage,
            GameObject owner, bool authoritative)
        {
            EnsureResolved();
            var prefab = _projectile;
            GameObject go;
            if (prefab != null)
            {
                go = Object.Instantiate(prefab, origin, Quaternion.LookRotation(direction));
            }
            else
            {
                // Missing art must not stop the bolt from existing and dealing damage.
                go = new GameObject("MagicianProjectile(Fallback)");
                go.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction));
            }

            var bolt = go.AddComponent<MagicBolt>();
            bolt.Init(direction.normalized, speed, damage, owner, authoritative, HitPrefabName);
        }

        static void SpawnMuzzle(Vector3 origin, Vector3 direction)
        {
            // Plain Play, not PlayShared: this runs on the master from Fire and on every other
            // client from SpawnVisual, so each machine already reaches it exactly once. Relaying it
            // as well would double the sound on the host.
            //
            // Ahead of the prefab check on purpose - missing muzzle art should not silence the cast.
            GameSfx.Play(SfxId.MagicCast, origin);

            EnsureResolved();
            var prefab = _muzzle;
            if (prefab == null)
                return;
            var go = Object.Instantiate(prefab, origin, Quaternion.LookRotation(direction));
            Object.Destroy(go, 2f);
        }

        public static void PlayHit(Vector3 position, Vector3 normal)
        {
            // Every client flies its own copy of the bolt and pops it on the same geometry, so the
            // impact is already happening everywhere. Local play only.
            GameSfx.Play(SfxId.MagicImpact, position);

            EnsureResolved();
            var prefab = _hit;
            if (prefab == null)
                return;
            var go = Object.Instantiate(prefab, position, Quaternion.LookRotation(normal));
            Object.Destroy(go, 3f);
        }

        public static int HitMask()
        {
            if (_hitMask != -1)
                return _hitMask;
            // The world sweep is for LEVEL GEOMETRY only. Player is excluded on purpose: player
            // damage comes from the anchor-proximity check, not this cast. If Player were included,
            // the bolt would detonate on the player's own solid collider a hair before the damage
            // check ran, taking the "hit a wall" branch and dealing no damage - which is exactly
            // the "it hits me but doesn't hurt" bug. Enemy/effects are also out so bolts do not pop
            // on gore or other enemies.
            int ignore = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Player",
                "Weapons", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            _hitMask = ~ignore;
            return _hitMask;
        }

        public const float Radius = HitRadius;
        public const float Lifetime = MaxLifetime;
        public const float SpawnGrace = SpawnGraceDistance;

        static GameObject Load(string name)
        {
            var prefab = Resources.Load<GameObject>(name);
#if UNITY_EDITOR
            if (prefab == null)
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Resources/" + name + ".prefab");
#endif
            if (prefab == null && name == ProjectilePrefabName)
                Debug.LogWarning("[CollarCali] MagicianProjectile prefab missing. " +
                                 "Run Tools/CollarCali/Build Magician.");
            return prefab;
        }
    }

    /// <summary>
    /// The flying bolt. Sweeps forward each frame and, on the authoritative copy, damages the first
    /// player it passes through. Cosmetic copies just fly and pop on the same world geometry.
    /// </summary>
    public class MagicBolt : MonoBehaviour
    {
        Vector3 _dir;
        float _speed;
        float _damage;
        GameObject _owner;
        bool _authoritative;
        string _hitPrefab;
        float _dieAt;
        bool _done;
        SfxLoop _travel;

        /// <summary>Metres flown so far, for the spawn grace above.</summary>
        float _travelled;

        public void Init(Vector3 dir, float speed, float damage, GameObject owner,
            bool authoritative, string hitPrefab)
        {
            _dir = dir;
            _speed = speed;
            _damage = damage;
            _owner = owner;
            _authoritative = authoritative;
            _hitPrefab = hitPrefab;
            _dieAt = Time.time + MagicProjectileFx.Lifetime;

            // Follows the bolt, so a shot passing you sweeps across the stereo field. Local, like
            // the rest of the bolt's presentation.
            _travel = GameSfx.PlayLoop(SfxId.MagicTravelLoop, transform);
        }

        void Update()
        {
            if (_done)
                return;

            float step = _speed * Time.deltaTime;
            var from = transform.position;
            var to = from + _dir * step;

            // Two independent checks along this frame's segment, nearest one wins:
            //  - world geometry, via a trigger-ignoring SphereCast (walls, floors)
            //  - players, by distance to their network anchor rather than a collider
            // The anchor check is why the bolt does not depend on a solid proxy collider the way a
            // raycast would; it damages remote players the same way the zombie melee routes damage.
            bool worldHit = Physics.SphereCast(from, MagicProjectileFx.Radius, _dir, out var wHit, step,
                MagicProjectileFx.HitMask(), QueryTriggerInteraction.Ignore);

            if (worldHit && _owner != null && wHit.collider != null &&
                wHit.collider.transform.IsChildOf(_owner.transform))
            {
                // Ignore the caster's own body during the first metre.
                worldHit = false;
            }

            // A sweep that begins already touching a surface reports distance 0 and a zero normal.
            // Treating that as an impact detonated the bolt on its first frame and left the hit
            // effect facing nowhere, so it is discarded rather than trusted.
            if (worldHit && (wHit.distance <= 0.0001f || wHit.normal.sqrMagnitude < 0.0001f))
                worldHit = false;

            // The cosmetic copies on other clients are spawned without an owner reference, so the
            // check above cannot protect them - this is what keeps a remote bolt from bursting on
            // the caster it just left.
            if (worldHit && _travelled < MagicProjectileFx.SpawnGrace)
                worldHit = false;

            float worldDist = worldHit ? wHit.distance : float.MaxValue;

            float playerDist = float.MaxValue;
            FpsNetworkBridge hitBridge = null;
            PlayerStats hitStats = null;
            Vector3 playerPoint = to;

            if (_authoritative && _damage > 0f)
                playerDist = FindPlayerAlongSegment(from, to, out hitBridge, out hitStats, out playerPoint);

            if (playerDist <= step && playerDist <= worldDist)
            {
                DamagePlayer(hitBridge, hitStats);
                Impact(playerPoint, -_dir, playHit: true);
                return;
            }

            if (worldHit)
            {
                Impact(wHit.point, wHit.normal, playHit: true);
                return;
            }

            transform.position = to;
            _travelled += step;
            if (Time.time >= _dieAt)
                Expire();
        }

        /// <summary>
        /// Closest point at which the segment passes within hit range of a living player's anchor.
        /// Returns float.MaxValue when none. Collider-free, so it works for remote proxies whose
        /// only network collider is a trigger.
        /// </summary>
        static float FindPlayerAlongSegment(Vector3 a, Vector3 b, out FpsNetworkBridge bridge,
            out PlayerStats stats, out Vector3 point)
        {
            bridge = null;
            stats = null;
            point = b;

            float best = float.MaxValue;
            var seg = b - a;
            float segLen = seg.magnitude;
            if (segLen < 0.0001f)
                return best;
            var segDir = seg / segLen;
            // Raised to make up for the much smaller sweep radius: the bolt has to stay just as easy
            // to be hit BY, even though it is now much harder for it to clip scenery.
            const float bodyRadius = 0.6f;
            float reach = MagicProjectileFx.Radius + bodyRadius;

            var bridges = Object.FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None);
            if (bridges != null && bridges.Length > 0)
            {
                foreach (var br in bridges)
                {
                    if (br == null || br.IsDead || br.Object == null || !br.Object.IsValid)
                        continue;
                    var anchor = br.GetNetworkAnchorPosition() + Vector3.up * 0.9f;
                    float t = Mathf.Clamp(Vector3.Dot(anchor - a, segDir), 0f, segLen);
                    var closest = a + segDir * t;
                    if ((anchor - closest).sqrMagnitude <= reach * reach && t < best)
                    {
                        best = t; bridge = br; stats = null; point = closest;
                    }
                }
                return best;
            }

            foreach (var st in Object.FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (st == null || st.IsDead || !st.gameObject.activeInHierarchy)
                    continue;
                var anchor = st.transform.position + Vector3.up * 0.9f;
                float t = Mathf.Clamp(Vector3.Dot(anchor - a, segDir), 0f, segLen);
                var closest = a + segDir * t;
                if ((anchor - closest).sqrMagnitude <= reach * reach && t < best)
                {
                    best = t; stats = st; bridge = null; point = closest;
                }
            }
            return best;
        }

        void DamagePlayer(FpsNetworkBridge bridge, PlayerStats stats)
        {
            if (bridge != null && !bridge.IsDead)
                bridge.TryRouteDamage(_damage, false);
            else if (stats != null && !stats.IsDead)
                stats.Damage(_damage, false);
        }

        void Impact(Vector3 point, Vector3 normal, bool playHit)
        {
            _done = true;
            if (playHit)
                MagicProjectileFx.PlayHit(point, normal);
            Cleanup();
        }

        void Expire()
        {
            _done = true;
            Cleanup();
        }

        void Cleanup()
        {
            // Stopped explicitly rather than left to the voice noticing its target was destroyed,
            // which would leave the whistle hanging for a frame past the impact.
            _travel.Stop();

            // Let a trailing particle finish rather than snapping the whole thing off instantly.
            foreach (var ps in GetComponentsInChildren<ParticleSystem>())
            {
                if (ps == null)
                    continue;
                ps.transform.SetParent(null, true);
                var main = ps.main;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Object.Destroy(ps.gameObject, main.duration + main.startLifetime.constantMax + 0.5f);
            }
            Destroy(gameObject);
        }
    }
}
