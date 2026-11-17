using System;
using System.Collections.Generic;
using MalbersAnimations;
using MalbersAnimations.Weapons;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// How this body's bones are turned against Steve's, the body Malbers wrote its bone offsets for.
    /// Malbers' weapon aim profiles (IKProfile) and a few IK processors turn a bone by a fixed rotation
    /// in that bone's own axes. This body's bones point differently, so Steve's numbers fold its arms
    /// when it aims a bow, and twist its spine when it carries a box.
    ///
    /// For each humanoid bone this keeps the turn from Steve's bone to this body's in the T-pose (and
    /// the same for the bone's parent). Animation moves both skeletons alike from the T-pose, so an
    /// offset converted with that turn moves this body as it moves Steve. PlayerCharacterBuilder fills
    /// in the turns and converts the IK manager's offsets once; the aim profile of every weapon this
    /// character draws is converted here as it is drawn, so pickups need nothing of their own.
    ///
    /// The hands: Malbers places a weapon's grip, and the bow string drawn to the other hand, at a
    /// fixed offset from the weapon manager's equip points, written for Steve's hands. The builder
    /// gives those equip points Steve's hand axes; this scales each offset by this body's hand against
    /// his as the weapon is drawn, and puts Malbers' own numbers back when it leaves the hand, so a
    /// dropped weapon still fits whoever picks it up.
    /// </summary>
    [DisallowMultipleComponent]
    public class BodyAxisCorrection : MonoBehaviour
    {
        [Serializable]
        public struct Entry
        {
            public HumanBodyBones bone;

            [Tooltip("Steve's bone to this body's, in the T-pose: this bone = Steve's bone x Turn.")]
            public Quaternion turn;

            [Tooltip("The same for the bone's parent.")]
            public Quaternion parentTurn;
        }

        /// <summary>How a Malbers offset is applied to its bone.</summary>
        public enum Kind
        {
            /// <summary>Added to the bone's own rotation (bone = bone x offset).</summary>
            Additive,
            /// <summary>Replaces the bone's rotation against its parent (bone = parent x offset).</summary>
            Override,
            /// <summary>Placed after a rotation that is not the skeleton's: the root, the aim, the world.</summary>
            Placed,
        }

        [SerializeField] List<Entry> bones = new List<Entry>();

        [Tooltip("This body's left hand against Steve's, wrist to middle knuckle, each in its own hand bone's units. " +
                 "Malbers' left-hand offsets (a bow's grip, the string drawn by a left-handed bow) are scaled by it.")]
        [SerializeField] float leftHandSize = 1f;

        [Tooltip("The same for the right hands: a sword's grip, the bow string drawn to the hand.")]
        [SerializeField] float rightHandSize = 1f;

        /// <summary>
        /// Malbers' own hand offsets of every weapon a body with this component has held, so each body
        /// scales them from his numbers and they go back when the weapon leaves the hand.
        /// </summary>
        static readonly Dictionary<MWeapon, (TransformOffset right, TransformOffset left, Vector3 knot)> SteveHands =
            new Dictionary<MWeapon, (TransformOffset, TransformOffset, Vector3)>();

        readonly Dictionary<IKProfile, IKProfile> _profiles = new Dictionary<IKProfile, IKProfile>();
        readonly HashSet<IKProfile> _converted = new HashSet<IKProfile>();
        Dictionary<HumanBodyBones, Entry> _byBone;
        MWeaponManager _weapons;

        /// <summary>The bones with a turn.</summary>
        public int Count => bones.Count;

        /// <summary>This body's left and right hands against Steve's (wrist to middle knuckle).</summary>
        public Vector2 HandSizes => new Vector2(leftHandSize, rightHandSize);

        void Awake()
        {
            _weapons = GetComponentInChildren<MWeaponManager>(true);
            if (_weapons == null)
                return;

            // The weapon manager swaps in its own per-weapon profiles after a weapon is drawn, so they are
            // converted too, once.
            Convert(_weapons.CustomIKWeaponsLeft);
            Convert(_weapons.CustomIKWeaponsRight);
            // Malbers tells this before it parents the weapon to the hand and applies the hand offset.
            _weapons.OnEquipWeapon.AddListener(OnWeaponEquipped);
            _weapons.OnUnequipWeapon.AddListener(OnWeaponUnequipped);
        }

        void OnDestroy()
        {
            if (_weapons != null)
            {
                _weapons.OnEquipWeapon.RemoveListener(OnWeaponEquipped);
                _weapons.OnUnequipWeapon.RemoveListener(OnWeaponUnequipped);
                var held = _weapons.Weapon;
                if (held != null)
                    PutBackHands(held);
            }

            foreach (var copy in _profiles.Values)
            {
                if (copy != null)
                    Destroy(copy);
            }
        }

        void OnWeaponEquipped(GameObject weapon)
        {
            var mweapon = weapon != null ? weapon.GetComponent<MWeapon>() : null;
            if (mweapon == null)
                return;
            mweapon.AimIKRight = Adapt(mweapon.AimIKRight);
            mweapon.AimIKLeft = Adapt(mweapon.AimIKLeft);
            FitHands(mweapon);
        }

        void OnWeaponUnequipped(GameObject weapon)
        {
            var mweapon = weapon != null ? weapon.GetComponent<MWeapon>() : null;
            if (mweapon != null)
                PutBackHands(mweapon);
        }

        /// <summary>
        /// The weapon's grip and bow string, from Malbers' numbers for Steve's hands, scaled to this
        /// body's: the equip points already carry his hand axes, so only the distances change.
        /// </summary>
        void FitHands(MWeapon weapon)
        {
            if (!SteveHands.TryGetValue(weapon, out var steve))
            {
                var gone = new List<MWeapon>();
                foreach (var key in SteveHands.Keys)
                {
                    if (key == null)
                        gone.Add(key);
                }

                foreach (var key in gone)
                    SteveHands.Remove(key);
                steve = (weapon.RightHandOffset, weapon.LeftHandOffset, weapon is MBow b ? b.KnotHandOffset : Vector3.zero);
                SteveHands[weapon] = steve;
            }

            var right = steve.right;
            right.Position *= rightHandSize;
            weapon.RightHandOffset = right;
            var left = steve.left;
            left.Position *= leftHandSize;
            weapon.LeftHandOffset = left;
            // The string is drawn by the hand the bow is not in.
            if (weapon is MBow bow)
                bow.KnotHandOffset = steve.knot * (bow.IsRightHanded ? leftHandSize : rightHandSize);
        }

        static void PutBackHands(MWeapon weapon)
        {
            if (!SteveHands.TryGetValue(weapon, out var steve))
                return;
            weapon.RightHandOffset = steve.right;
            weapon.LeftHandOffset = steve.left;
            if (weapon is MBow bow)
                bow.KnotHandOffset = steve.knot;
        }

        void Convert(List<IDPair<WeaponID, IKProfile>> overrides)
        {
            if (overrides == null)
                return;
            foreach (var pair in overrides)
            {
                if (pair != null)
                    pair.Value = Adapt(pair.Value);
            }
        }

        bool TryGet(HumanBodyBones bone, out Entry entry)
        {
            if (_byBone == null)
            {
                _byBone = new Dictionary<HumanBodyBones, Entry>();
                foreach (var e in bones)
                    _byBone[e.bone] = e;
            }

            return _byBone.TryGetValue(bone, out entry);
        }

        /// <summary>The turn from Steve's bone to this body's, when this body has the bone.</summary>
        public bool TryGetTurn(HumanBodyBones bone, out Quaternion turn)
        {
            bool found = TryGet(bone, out var entry);
            turn = found ? entry.turn : Quaternion.identity;
            return found;
        }

        /// <summary>Steve's offset for one bone, as this body needs it.</summary>
        public Vector3 Convert(HumanBodyBones bone, Vector3 steveOffset, Kind kind)
        {
            if (!TryGet(bone, out var e))
                return steveOffset;

            var offset = Quaternion.Euler(steveOffset);
            var ours = kind switch
            {
                Kind.Additive => Quaternion.Inverse(e.turn) * offset * e.turn,
                Kind.Override => Quaternion.Inverse(e.parentTurn) * offset * e.turn,
                _ => offset * e.turn,
            };
            return ours.eulerAngles;
        }

        /// <summary>
        /// A copy of a Malbers aim profile with its bone offsets for this body: the same copy every
        /// time, and a profile that is already one of the copies comes back as it is.
        /// </summary>
        public IKProfile Adapt(IKProfile source)
        {
            if (source == null || bones.Count == 0 || _converted.Contains(source))
                return source;
            if (_profiles.TryGetValue(source, out var copy) && copy != null)
                return copy;

            copy = Instantiate(source);
            copy.name = source.name + " (" + name + ")";
            if (copy.offsets != null)
            {
                foreach (var offset in copy.offsets)
                {
                    if (offset != null)
                        offset.RotationOffset = Convert(offset.bone, offset.RotationOffset, KindOf(offset.rotationType));
                }
            }

            _profiles[source] = copy;
            _converted.Add(copy);
            return copy;
        }

        static Kind KindOf(BoneOfsset.IKType type) => type switch
        {
            BoneOfsset.IKType.AdditiveOffset => Kind.Additive,
            BoneOfsset.IKType.OffsetOnly => Kind.Override,
            _ => Kind.Placed,
        };

#if UNITY_EDITOR
        /// <summary>Editor: the builder's turns, bone by bone.</summary>
        public void SetTurns(List<Entry> entries)
        {
            bones = entries ?? new List<Entry>();
            _byBone = null;
        }
#endif
    }
}
