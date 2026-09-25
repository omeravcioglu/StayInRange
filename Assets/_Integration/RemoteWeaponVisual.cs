#if CMPSETUP_COMPLETE
using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Third-person held-weapon mesh for remote players (proxies).
    /// Uses Weapon_SO.pickUpGraphics parented to the right hand.
    /// </summary>
    public class RemoteWeaponVisual : MonoBehaviour
    {
        [SerializeField] Transform handBone;
        [SerializeField] Vector3 localPosition = new Vector3(0.05f, 0.02f, 0.1f);
        [SerializeField] Vector3 localEuler = new Vector3(0f, 90f, 90f);
        [SerializeField] Vector3 localScale = new Vector3(0.85f, 0.85f, 0.85f);
        [SerializeField] Transform muzzlePoint;

        GameObject _currentVisual;
        GameObject _flashlightRig;
        bool _flashlightOn = true;
        string _currentWeaponName = string.Empty;
        Weapon_SO _currentWeapon;

        public Transform MuzzlePoint => muzzlePoint != null ? muzzlePoint : (_currentVisual != null ? _currentVisual.transform : handBone);
        public Weapon_SO CurrentWeapon => _currentWeapon;

        public void Initialize(Transform remoteBody)
        {
            if (handBone == null && remoteBody != null)
                handBone = FindDeep(remoteBody, "bip R Hand");

            // Any humanoid body (the Meshy characters included) exposes its hand through the avatar,
            // so resolve it generically when the named bone is not this particular rig's.
            if (handBone == null)
            {
                var animator = GetComponent<Animator>();
                if (animator != null && animator.isHuman)
                    handBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            }

            if (handBone == null)
                handBone = transform;
        }

        public void SetWeapon(string weaponName)
        {
            if (_currentWeaponName == weaponName)
                return;

            _currentWeaponName = weaponName ?? string.Empty;
            ClearVisual();

            var weapon = WeaponCatalog.FindByName(_currentWeaponName);
            _currentWeapon = weapon;
            if (weapon == null || weapon.pickUpGraphics == null || handBone == null)
                return;

            _currentVisual = Instantiate(weapon.pickUpGraphics, handBone);
            _currentVisual.name = "RemoteWeapon_" + weapon.name;
            // The Meshy humanoid bones carry a large world scale, so a plain local scale/offset blows
            // the gun up to map size. Divide the intended world-space scale and offset by the hand's
            // lossyScale so the weapon lands at a sane size in the hand on any rig.
            var handScale = handBone.lossyScale;
            _currentVisual.transform.localPosition = InvScale(localPosition, handScale);
            _currentVisual.transform.localRotation = Quaternion.Euler(localEuler);
            _currentVisual.transform.localScale = InvScale(localScale, handScale);

            // Disable interactive/physics leftovers from world pickups.
            foreach (var col in _currentVisual.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (var rb in _currentVisual.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }

            muzzlePoint = FindDeep(_currentVisual.transform, "Muzzle")
                          ?? FindDeep(_currentVisual.transform, "FirePoint")
                          ?? _currentVisual.transform;
            AttachFlashlight();
        }

        public void SetFlashlight(bool on)
        {
            _flashlightOn = on;
            if (_flashlightRig == null)
                AttachFlashlight();
            ApplyFlashlightState();
        }

        void AttachFlashlight()
        {
            if (_flashlightRig != null)
                Destroy(_flashlightRig);

            var parent = MuzzlePoint != null ? MuzzlePoint : transform;
            _flashlightRig = WeaponFlashlight.CreateRig(parent, new Vector3(0f, 0f, 0.08f));
            ApplyFlashlightState();
        }

        void ApplyFlashlightState()
        {
            if (_flashlightRig == null)
                return;
            _flashlightRig.SetActive(_flashlightOn);
        }

        public void PlayMuzzleFlash()
        {
            if (_currentWeapon == null)
                return;

            var point = MuzzlePoint != null ? MuzzlePoint : transform;

            if (_currentWeapon.muzzleVFX != null)
            {
                var vfx = Instantiate(_currentWeapon.muzzleVFX, point.position, point.rotation);
                Destroy(vfx, 2f);
            }

            PlayFireSound(point.position);
        }

        /// <summary>
        /// A teammate's shot, positioned in the world.
        ///
        /// Cowsins only plays a fire sound for the weapon in your own hands - it goes through
        /// SoundManager as a 2D one-shot from the local WeaponIdentification - so before this the
        /// other player's gun was completely silent on your machine. The clip comes straight from
        /// their Weapon_SO rather than from anything generated, so the same rifle sounds the same
        /// whoever is firing it; only the placement and falloff differ.
        /// </summary>
        void PlayFireSound(Vector3 position)
        {
            var clips = _currentWeapon.audioSFX != null ? _currentWeapon.audioSFX.shooting : null;
            if (clips == null || clips.Length == 0)
                return;

            var clip = clips[Random.Range(0, clips.Length)];
            if (clip == null)
                return;

            // Quieter than a first-person shot and audible across a room: gunfire is how you keep
            // track of where the other player is fighting.
            GameSfx.PlayClip(clip, position, 0.7f,
                _currentWeapon.pitchVariationFiringSFX, 8f, 80f);
        }

        void ClearVisual()
        {
            if (_flashlightRig != null)
                Destroy(_flashlightRig);
            _flashlightRig = null;
            if (_currentVisual != null)
                Destroy(_currentVisual);
            _currentVisual = null;
            muzzlePoint = null;
        }

        // Divides a desired world-space vector by a parent's lossyScale so, once parented, the child's
        // effective world value matches the original intent regardless of the parent's scale.
        static Vector3 InvScale(Vector3 v, Vector3 scale)
        {
            return new Vector3(
                v.x / Mathf.Max(1e-4f, Mathf.Abs(scale.x)) * Mathf.Sign(scale.x == 0f ? 1f : scale.x),
                v.y / Mathf.Max(1e-4f, Mathf.Abs(scale.y)) * Mathf.Sign(scale.y == 0f ? 1f : scale.y),
                v.z / Mathf.Max(1e-4f, Mathf.Abs(scale.z)) * Mathf.Sign(scale.z == 0f ? 1f : scale.z));
        }

        static Transform FindDeep(Transform parent, string name)
        {
            if (parent == null)
                return null;
            if (parent.name == name)
                return parent;
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child != parent && child.name == name)
                    return child;
            }
            return null;
        }
    }
}
#endif
