using System.Collections.Generic;
using Fusion;
using MalbersAnimations.Weapons;
using UnityEngine;

namespace CollarCali
{
    /// <summary>One Malbers weapon on a third-person player's body, as the others are told about it.</summary>
    public struct BodyWeapon : INetworkStruct
    {
        /// <summary>Which of PlayerRigRecipe.remoteWeapons, counted from 1; 0 is an empty slot.</summary>
        public short Prefab;

        /// <summary>The humanoid bone it hangs from (HumanBodyBones): a hand when drawn, a holster's bone when put away.</summary>
        public byte Bone;

        /// <summary>Its place against that bone, in the bone's own space.</summary>
        public Vector3 Position;

        public Quaternion Rotation;

        /// <summary>Its scale against the bone's (a holstered sword is drawn smaller).</summary>
        public float Scale;
    }

    /// <summary>
    /// The weapons the others see on a third-person player: the one in hand and the ones in holsters.
    /// The owner lists every Malbers weapon on its character - which prefab, the bone it hangs from and
    /// where it sits against that bone - and the other machines hang a copy of that prefab's meshes
    /// (nothing else: no colliders, no scripts) on the same bone of the body they see. Both bodies are
    /// the same model, so the weapon lands in the same hand at the same grip.
    /// </summary>
    public partial class PlayerNetworkBridge
    {
        const int BodyWeaponCapacity = 4;

        [Networked, Capacity(BodyWeaponCapacity)] NetworkArray<BodyWeapon> BodyWeapons => default;

        /// <summary>For editor checks only: also hang the weapons on the owner's own hidden body, to picture it.</summary>
        public static bool MirrorWeaponsOnOwner;

        readonly List<MWeapon> _weaponScratch = new List<MWeapon>();
        readonly Dictionary<MWeapon, short> _weaponPrefabIndex = new Dictionary<MWeapon, short>();
        Dictionary<Transform, HumanBodyBones> _ownBones;
        readonly GameObject[] _weaponVisuals = new GameObject[BodyWeaponCapacity];
        readonly short[] _weaponVisualPrefab = new short[BodyWeaponCapacity];

        /// <summary>Owner: the Malbers weapons on the character now, in hand or holstered.</summary>
        void CaptureBodyWeapons()
        {
            var player = _tps;
            var animator = player != null && player.Animal != null ? player.Animal.Anim : null;
            if (animator == null || !player.isActiveAndEnabled)
                return;

            if (_ownBones == null)
            {
                _ownBones = new Dictionary<Transform, HumanBodyBones>();
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    var t = animator.GetBoneTransform(b);
                    if (t != null && !_ownBones.ContainsKey(t))
                        _ownBones[t] = b;
                }
            }

            player.GetComponentsInChildren(false, _weaponScratch);
            int slot = 0;
            foreach (var weapon in _weaponScratch)
            {
                if (slot >= BodyWeaponCapacity)
                    break;
                short prefab = PrefabIndexOf(weapon);
                if (prefab == 0)
                    continue;

                var bone = weapon.transform.parent;
                while (bone != null && !_ownBones.ContainsKey(bone))
                    bone = bone.parent;
                if (bone == null)
                    continue;

                var t = weapon.transform;
                var entry = new BodyWeapon
                {
                    Prefab = prefab,
                    Bone = (byte)_ownBones[bone],
                    Position = bone.InverseTransformPoint(t.position),
                    Rotation = Quaternion.Inverse(bone.rotation) * t.rotation,
                    Scale = Mathf.Abs(bone.lossyScale.x) > 1e-5f ? t.lossyScale.x / bone.lossyScale.x : 1f,
                };
                if (!Same(BodyWeapons.Get(slot), entry))
                    BodyWeapons.Set(slot, entry);
                slot++;
            }

            for (; slot < BodyWeaponCapacity; slot++)
            {
                if (BodyWeapons.Get(slot).Prefab != 0)
                    BodyWeapons.Set(slot, default);
            }
        }

        static bool Same(BodyWeapon a, BodyWeapon b) =>
            a.Prefab == b.Prefab && a.Bone == b.Bone && (a.Position - b.Position).sqrMagnitude < 1e-8f &&
            Quaternion.Dot(a.Rotation, b.Rotation) > 0.99999f && Mathf.Abs(a.Scale - b.Scale) < 1e-4f;

        /// <summary>The weapon's place in PlayerRigRecipe.remoteWeapons, from 1; 0 when the others could not show it.</summary>
        short PrefabIndexOf(MWeapon weapon)
        {
            if (_weaponPrefabIndex.TryGetValue(weapon, out short known))
                return known;

            short index = 0;
            var recipe = PlayerRigRecipe.Load();
            var prefabs = recipe != null ? recipe.remoteWeapons : null;
            if (prefabs != null)
            {
                string name = weapon.name.Replace("(Clone)", string.Empty).Trim();
                for (int i = 0; i < prefabs.Length && i < short.MaxValue - 1; i++)
                {
                    if (prefabs[i] != null && prefabs[i].name == name)
                    {
                        index = (short)(i + 1);
                        break;
                    }
                }
            }

            _weaponPrefabIndex[weapon] = index;
            return index;
        }

        /// <summary>Other machines: the owner's weapons, hung on the body they see.</summary>
        void ApplyBodyWeapons()
        {
            if (HasStateAuthority && !MirrorWeaponsOnOwner)
                return;
            if (_remoteAnimator == null)
                return;

            var recipe = PlayerRigRecipe.Load();
            var prefabs = recipe != null ? recipe.remoteWeapons : null;
            for (int i = 0; i < BodyWeaponCapacity; i++)
            {
                var entry = BodyWeapons.Get(i);
                if (entry.Prefab != _weaponVisualPrefab[i])
                {
                    if (_weaponVisuals[i] != null)
                        Destroy(_weaponVisuals[i]);
                    _weaponVisuals[i] = null;
                    _weaponVisualPrefab[i] = entry.Prefab;
                    if (entry.Prefab > 0 && prefabs != null && entry.Prefab <= prefabs.Length && prefabs[entry.Prefab - 1] != null)
                        _weaponVisuals[i] = MakeWeaponVisual(prefabs[entry.Prefab - 1]);
                }

                var visual = _weaponVisuals[i];
                if (visual == null)
                    continue;

                var bone = _remoteAnimator.GetBoneTransform((HumanBodyBones)entry.Bone);
                if (bone == null)
                {
                    visual.SetActive(false);
                    continue;
                }

                var t = visual.transform;
                if (t.parent != bone)
                    t.SetParent(bone, false);
                t.SetLocalPositionAndRotation(entry.Position, entry.Rotation);
                t.localScale = Vector3.one * entry.Scale;
                if (!visual.activeSelf)
                    visual.SetActive(true);
            }
        }

        void DestroyBodyWeapons()
        {
            for (int i = 0; i < BodyWeaponCapacity; i++)
            {
                if (_weaponVisuals[i] != null)
                    Destroy(_weaponVisuals[i]);
                _weaponVisuals[i] = null;
                _weaponVisualPrefab[i] = 0;
            }
        }

        /// <summary>
        /// A copy of a weapon prefab with only its meshes: made under an inactive parent, so none of its
        /// scripts wake, and stripped of everything that is not a transform or a renderer.
        /// </summary>
        static GameObject MakeWeaponVisual(GameObject prefab)
        {
            var holder = new GameObject("Weapon visual (building)");
            holder.SetActive(false);
            var copy = Instantiate(prefab, holder.transform);
            copy.name = prefab.name + " (remote)";

            // Scripts first (they are what require the rest), then particle systems, then anything else
            // that is not a mesh - in that order nothing is still required when it goes.
            foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                DestroyImmediate(script);
            foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true))
                DestroyImmediate(particles);
            foreach (var component in copy.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is MeshFilter ||
                    component is MeshRenderer || component is SkinnedMeshRenderer)
                    continue;
                DestroyImmediate(component);
            }

            copy.transform.SetParent(null, false);
            Destroy(holder);
            return copy;
        }
    }
}
