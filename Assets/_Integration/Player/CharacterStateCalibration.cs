using MalbersAnimations.Controller;
using MalbersAnimations.Scriptables;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fits Malbers' climbing to the player's body, from the body profile, and keeps it fitted.
    /// Lives on the character's MAnimal (PlayerCharacter.prefab carries it).
    ///
    /// WHAT IT CHANGES, on this character's runtime copies of the states:
    ///   - Free Climb casts for the wall from the chest and the hips (ClimbChest, ClimbHip);
    ///   - Ledge Grab hangs the body below a ledge by the reach of its raised arms (each profile's
    ///     AlignOffset), so the hands land on the ledge rather than the face;
    ///   - Wall Run and Wall Run Vertical check for the wall from hip height (Center);
    ///   - all four find walls and ledges on the profile's surface layers. Malbers ships them seeing
    ///     the Default layer only, while the levels' walls are on Metal, Grass, Wood and the rest.
    /// Malbers' numbers were tuned for Steve; each is Steve's value scaled by the profile's Climbing
    /// section (how the player's chest, hips and reach compare with his), so an update of Malbers'
    /// states still comes through. Edit the profile in play mode and the change shows at once.
    ///
    /// Malbers clones the states per character; everything here is written to those copies, as
    /// constants, never to the shared assets (see ThirdPersonJumpTuning).
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterStateCalibration : MonoBehaviour
    {
        [Tooltip("The body profile the numbers come from (Assets/_Integration/Characters/Player).")]
        public CharacterBodyProfile profile;

        MAnimal _animal;
        Climb _climb;
        LedgeGrab _ledge;
        WallRun _wallRun;
        WallRunVertical _wallRunVertical;
        bool _resolved, _refusedShared;

        // Steve's values, read once from the copies before anything is changed.
        Vector3 _climbChest, _climbHip, _wallRunCenter, _verticalCenter;
        Vector2[] _ledgeAlign;

        (float chest, float hip, float reach, int surfaces) _applied = (-1f, -1f, -1f, 0);

        void Update()
        {
            if (profile == null || !Resolve())
                return;

            var numbers = profile.climbing;
            var wanted = (Scale(numbers.chestScale), Scale(numbers.hipScale), Scale(numbers.reachScale), profile.surfaces.value);
            if (wanted == _applied)
                return;

            Apply(wanted.Item1, wanted.Item2, wanted.Item3, wanted.Item4);
            _applied = wanted;
        }

        /// <summary>0 means not measured yet: Steve's own value.</summary>
        static float Scale(float value) => value > 0f ? value : 1f;

        bool Resolve()
        {
            if (_resolved)
                return true;
            if (_refusedShared)
                return false;

            if (_animal == null)
                _animal = GetComponent<MAnimal>();
            if (_animal == null)
                return false;

            if (!_animal.CloneStates)
            {
                _refusedShared = true;
                Debug.LogWarning("[CollarCali] " + _animal.name + " does not clone its Malbers states, so its " +
                                 "climbing is left as shipped rather than changing the shared state assets.", this);
                return false;
            }

            _climb = _animal.State_Get<Climb>();
            _ledge = _animal.State_Get<LedgeGrab>();
            _wallRun = _animal.State_Get<WallRun>();
            _wallRunVertical = _animal.State_Get<WallRunVertical>();
            if (_climb == null && _ledge == null && _wallRun == null && _wallRunVertical == null)
                return false; // MAnimal has not woken up yet

            if (_climb != null)
            {
                _climbChest = _climb.ClimbChest;
                _climbHip = _climb.ClimbHip;
            }

            if (_ledge != null)
            {
                _ledgeAlign = new Vector2[_ledge.profiles.Count];
                for (int i = 0; i < _ledgeAlign.Length; i++)
                    _ledgeAlign[i] = _ledge.profiles[i].AlignOffset;
            }

            if (_wallRun != null)
                _wallRunCenter = _wallRun.Center.Value;
            if (_wallRunVertical != null)
                _verticalCenter = _wallRunVertical.Center.Value;

            _resolved = true;
            return true;
        }

        void Apply(float chest, float hip, float reach, LayerMask surfaces)
        {
            if (_climb != null)
            {
                _climb.ClimbChest = Raised(_climbChest, chest);
                _climb.ClimbHip = Raised(_climbHip, hip);
                _climb.ClimbLayer = Local(_climb.ClimbLayer, surfaces);
            }

            if (_ledge != null)
            {
                for (int i = 0; i < _ledge.profiles.Count && i < _ledgeAlign.Length; i++)
                    _ledge.profiles[i].AlignOffset = new Vector2(_ledgeAlign[i].x, _ledgeAlign[i].y * reach);
                _ledge.LedgeLayer = Local(_ledge.LedgeLayer, surfaces);
            }

            if (_wallRun != null)
            {
                _wallRun.Center = Local(_wallRun.Center, Raised(_wallRunCenter, hip));
                _wallRun.Layer = Local(_wallRun.Layer, surfaces);
            }

            if (_wallRunVertical != null)
            {
                _wallRunVertical.Center = Local(_wallRunVertical.Center, Raised(_verticalCenter, hip));
                _wallRunVertical.WallLayer = Local(_wallRunVertical.WallLayer, surfaces);
            }
        }

        static Vector3 Raised(Vector3 steve, float scale) => new Vector3(steve.x, steve.y * scale, steve.z);

        // Malbers' references read a shared variable asset unless told to use their own constant;
        // writing through one of those would change the asset for every character.
        static LayerReference Local(LayerReference reference, LayerMask value)
        {
            reference ??= new LayerReference();
            reference.UseConstant = true;
            reference.ConstantValue = value;
            return reference;
        }

        static Vector3Reference Local(Vector3Reference reference, Vector3 value)
        {
            reference ??= new Vector3Reference();
            reference.UseConstant = true;
            reference.ConstantValue = value;
            return reference;
        }
    }
}
