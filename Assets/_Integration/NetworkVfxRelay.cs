#if CMPSETUP_COMPLETE
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Shared-mode VFX/explosion relay living on the Game Manager NetworkObject.
    /// </summary>
    public class NetworkVfxRelay : NetworkBehaviour
    {
        public static NetworkVfxRelay Instance { get; private set; }

        public override void Spawned()
        {
            Instance = this;
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this)
                Instance = null;
        }

        public void BroadcastExplosion(Vector3 position, GameObject explosionVfxPrefab)
        {
            if (!Object || !Object.IsValid)
                return;

            var key = explosionVfxPrefab != null ? explosionVfxPrefab.name : string.Empty;
            RPC_Explosion(position, key);
        }

        /// <summary>
        /// Blood is broadcast rather than spawned per-machine because damage is applied on the
        /// master: without this only the host would ever see a zombie bleed.
        /// </summary>
        public void BroadcastBlood(Vector3 position, int kind, float scale)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_Blood(position, kind, scale);
        }

        /// <summary>
        /// A magician fired a bolt. The master has already spawned its authoritative, damage-dealing
        /// projectile locally; this tells every OTHER client to spawn a cosmetic copy that flies the
        /// same deterministic straight line, so both players see the same bolt.
        /// </summary>
        public void BroadcastProjectile(Vector3 origin, Vector3 direction, float speed)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_Projectile(origin, direction, speed);
        }

        public void BroadcastBarrelExplode(Vector3 position)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_BarrelExplode(position);
        }

        /// <summary>
        /// An enemy made a noise. Same reasoning as the blood: brains only run on the master, so a
        /// zombie's swing played locally there would be silent on every other screen.
        ///
        /// sceneKey is the hierarchy-path hash NetworkWorldActor binds by, and it is what lets the
        /// sound re-attach to the right body on the receiving client instead of being pinned to the
        /// position the enemy happened to be standing in when the RPC left.
        /// </summary>
        public void BroadcastSfx(int id, Vector3 position, int sceneKey)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_Sfx(id, position, sceneKey);
        }

        /// <summary>
        /// The master decided a hidden spawner should produce something. Every client has to create
        /// the same creature, with the same name, or NetworkWorldActor cannot bind the master's brain
        /// to it and it stands frozen for everybody else.
        ///
        /// Unlike the rest of this relay, this one DOES invoke locally: the master must run exactly
        /// the same spawn path as the clients rather than a parallel one of its own.
        ///
        /// One RPC for every kind of spawner - the variant int means whatever the receiving spawner
        /// says it means - rather than a near-identical message per enemy type.
        /// </summary>
        public void BroadcastHiddenSpawn(int spawnerKey, int sequence, Vector3 position, float yaw,
            int variant)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_HiddenSpawn(spawnerKey, sequence, position, yaw, variant);
        }

        [Rpc(RpcSources.All, RpcTargets.All)]
        void RPC_Explosion(Vector3 position, NetworkString<_32> vfxName)
        {
            PlayExplosionVfx(position, vfxName.ToString());
        }

        // InvokeLocal = false: the caller has already played the effect locally so the shooter
        // sees it on the same frame. Letting the RPC run on the sender too would double it up.
        // InvokeLocal = false: the master already spawned its own authoritative projectile, so it
        // must not also spawn the cosmetic one here or the bolt would be doubled on the host.
        [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
        void RPC_Projectile(Vector3 origin, Vector3 direction, float speed)
        {
            MagicProjectileFx.SpawnVisual(origin, direction, speed);
        }

        [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
        void RPC_Blood(Vector3 position, int kind, float scale)
        {
            ZombieBloodFx.Play(position, (ZombieBloodFx.Kind)kind, scale);
        }

        // InvokeLocal = false for the same reason as the others: the caller already played it on
        // its own machine, so running here too would double every enemy sound on the host.
        [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
        void RPC_Sfx(int id, Vector3 position, int sceneKey)
        {
            GameSfx.PlayFromNetwork((SfxId)id, position, sceneKey);
        }

        /// <summary>
        /// Retires a creature a hidden spawner produced, on every machine including this one.
        /// </summary>
        public void BroadcastHiddenDespawn(int spawnerKey, int sequence)
        {
            if (!Object || !Object.IsValid)
                return;
            RPC_HiddenDespawn(spawnerKey, sequence);
        }

        [Rpc(RpcSources.All, RpcTargets.All)]
        void RPC_HiddenDespawn(int spawnerKey, int sequence)
        {
            HiddenSpawnUtility.FindReceiverByKey(spawnerKey)?.DespawnFromNetwork(sequence);
        }

        // InvokeLocal is left at its default of true here, deliberately - see BroadcastHiddenSpawn.
        [Rpc(RpcSources.All, RpcTargets.All)]
        void RPC_HiddenSpawn(int spawnerKey, int sequence, Vector3 position, float yaw, int variant)
        {
            var spawner = HiddenSpawnUtility.FindReceiverByKey(spawnerKey);
            if (spawner == null)
            {
                // A spawner present on the master but not here means the scenes disagree, which is
                // worth a warning rather than a silently missing enemy.
                Debug.LogWarning("[CollarCali] Hidden spawn arrived for spawner key " + spawnerKey +
                                 " but no such spawner exists on this client.");
                return;
            }

            spawner.SpawnFromNetwork(sequence, position, yaw, variant);
        }

        [Rpc(RpcSources.All, RpcTargets.All)]
        void RPC_BarrelExplode(Vector3 position)
        {
            PlayExplosionVfx(position, string.Empty);
            DestroyNearbyBarrels(position, 0.75f);
        }

        static void PlayExplosionVfx(Vector3 position, string vfxName)
        {
            if (!string.IsNullOrEmpty(vfxName))
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.name != vfxName || go.scene.IsValid())
                        continue;
                    var instance = Instantiate(go, position, Quaternion.identity);
                    Destroy(instance, 5f);
                    return;
                }
            }

            // Fallback flash so remotes always see something.
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "ExplosionFlash";
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * 2f;
            var col = sphere.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            Destroy(sphere, 0.35f);
        }

        static void DestroyNearbyBarrels(Vector3 position, float radius)
        {
            foreach (var barrel in UnityEngine.Object.FindObjectsByType<cowsins.ExplosiveBarrel>(FindObjectsSortMode.None))
            {
                if (barrel == null)
                    continue;
                if (Vector3.Distance(barrel.transform.position, position) > radius)
                    continue;

                // Visual-only teardown on remotes (damage already applied by authority).
                UnityEngine.Object.Destroy(barrel.gameObject);
            }
        }
    }
}
#endif
