#if CMPSETUP_COMPLETE
using cowsins;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fusion-synced world weapon pickup. Uses Cowsins WeaponPickeable for local
    /// interact UX; ownership/despawn is networked.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkWeaponPickup : NetworkBehaviour
    {
        [Networked] public NetworkString<_32> WeaponName { get; set; }
        [Networked] public int CurrentBullets { get; set; }
        [Networked] public int TotalBullets { get; set; }
        [Networked] public NetworkBool Claimed { get; set; }

        WeaponPickeable _pickeable;
        bool _visualReady;
        bool _loggedMiss;

        public override void Spawned()
        {
            _pickeable = GetComponent<WeaponPickeable>();
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
                rb.isKinematic = true;
            ApplyWeaponDataToPickeable();
            _visualReady = true;
        }

        /// <summary>
        /// Networked state must be written from the spawn callback; Spawned() has already run by
        /// the time Runner.Spawn returns, so late writes leave WeaponName empty on every reader.
        /// </summary>
        public void WriteWeaponData(Weapon_SO weapon, int currentBullets, int totalBullets)
        {
            if (weapon == null)
                return;

            WeaponName = weapon.name;
            CurrentBullets = currentBullets;
            TotalBullets = totalBullets;
        }

        public void InitializeFromWeapon(Weapon_SO weapon, int currentBullets, int totalBullets)
        {
            if (HasStateAuthority)
                WriteWeaponData(weapon, currentBullets, totalBullets);

            _pickeable = GetComponent<WeaponPickeable>();
            // Spawned() may already have applied it; GetVisuals() rebuilds the mesh, so don't repeat.
            if (_pickeable != null && weapon != null && _pickeable.weapon != weapon)
                ApplyWeaponWithAmmo(weapon);
        }

        public override void Render()
        {
            if (!_visualReady)
                return;
            ApplyWeaponDataToPickeable();
        }

        void ApplyWeaponDataToPickeable()
        {
            if (_pickeable == null)
                _pickeable = GetComponent<WeaponPickeable>();
            if (_pickeable == null)
                return;

            var rawName = WeaponName.ToString();
            var weapon = WeaponCatalog.FindByName(rawName);
            // #region agent log
            if (weapon == null)
            {
                if (!_loggedMiss)
                {
                    _loggedMiss = true;
                    CollarCali.AgentDebugLog.Write("W1", "NetworkWeaponPickup.ApplyWeaponData", "catalog_miss",
                        "{\"raw\":\"" + (rawName ?? "") +
                        "\",\"len\":" + (rawName != null ? rawName.Length : 0) +
                        ",\"authority\":" + (HasStateAuthority ? "true" : "false") +
                        ",\"claimed\":" + (Claimed ? "true" : "false") + "}");
                }
                return;
            }
            // #endregion

            if (_pickeable.weapon != weapon)
            {
                ApplyWeaponWithAmmo(weapon);
                // #region agent log
                CollarCali.AgentDebugLog.Write("W1", "NetworkWeaponPickup.ApplyWeaponData", "weapon_applied",
                    "{\"name\":\"" + weapon.name +
                    "\",\"current\":" + CurrentBullets +
                    ",\"total\":" + TotalBullets + "}");
                // #endregion
            }
        }

        /// <summary>
        /// WeaponPickeable.Initialize() bails out when its weapon is null, which is always the case
        /// on a networked prefab at Awake. Assigning the weapon alone therefore leaves the pickup
        /// with zero ammo, so push the bullet counts in explicitly.
        /// </summary>
        void ApplyWeaponWithAmmo(Weapon_SO weapon)
        {
            int current = CurrentBullets;
            int total = TotalBullets;
            if (current <= 0)
                current = weapon.magazineSize;
            if (total <= 0)
                total = weapon.totalMagazines * Mathf.Max(1, weapon.magazineSize);

            _pickeable.DropOverrideParameters(weapon, current, total, _pickeable.currentAttachments);
        }

        /// <summary>
        /// Called from WeaponPickeable after local inventory accept, instead of Destroy.
        /// </summary>
        public bool TryNetworkPickup(Transform player)
        {
            if (!Object || !Object.IsValid || Claimed)
                return false;

            var bridge = player.GetComponentInParent<FpsNetworkBridge>()
                         ?? player.GetComponent<FpsNetworkBridge>();
            if (bridge == null)
                bridge = UnityEngine.Object.FindFirstObjectByType<FpsNetworkBridge>();

            // 2D: this only runs on the machine that picked the weapon up, and it is feedback about
            // your own inventory rather than an event in the world.
            GameSfx.Play2D(SfxId.WeaponPickup);

            // Local inventory was already updated by WeaponPickeable; request despawn.
            RPC_RequestClaim(Runner.LocalPlayer);
            return true;
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        void RPC_RequestClaim(PlayerRef requester)
        {
            if (Claimed)
                return;
            Claimed = true;
            Runner.Despawn(Object);
        }

        public static NetworkWeaponPickup SpawnDrop(
            NetworkRunner runner,
            NetworkObject prefab,
            Vector3 position,
            Quaternion rotation,
            Weapon_SO weapon,
            int currentBullets,
            int totalBullets)
        {
            if (runner == null || prefab == null || weapon == null)
                return null;

            var obj = runner.Spawn(prefab, position, rotation, runner.LocalPlayer,
                (r, spawned) =>
                {
                    var pending = spawned.GetComponent<NetworkWeaponPickup>();
                    pending?.WriteWeaponData(weapon, currentBullets, totalBullets);
                });
            if (obj == null)
                return null;

            var pickup = obj.GetComponent<NetworkWeaponPickup>();
            if (pickup != null)
                pickup.InitializeFromWeapon(weapon, currentBullets, totalBullets);
            return pickup;
        }
    }
}
#endif
