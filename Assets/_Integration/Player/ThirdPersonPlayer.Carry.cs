using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Carrying a body or prop with telekinesis, and the keys telekinesis needs to itself. The keys
    /// are muted in the character's shared lock set, so they come back only when nothing else mutes
    /// them.
    /// </summary>
    public partial class ThirdPersonPlayer
    {
        /// <summary>
        /// Malbers' inputs muted while something is held: both mouse buttons, the action keys,
        /// interact and Q - and R and the mouse wheel, which turn and tilt a held prop.
        /// </summary>
        public const string CarryBlockedInputs = "Action1,Action2,Action3,Action4,Interact,Ability1,Reload,Zoom,LockNextTarget";

        /// <summary>Muted while a body is merely targeted, so E lifts it and nothing else.</summary>
        public const string InteractInput = "Interact";

        readonly Hold _carryKeys = new Hold("carrying");
        readonly Hold _interactKey = new Hold("telekinesis has interact");

        bool _carrying;
        bool _interactClaimed;
        float _carrySpeed = 1f;
        bool _timeScaled;
        float _savedTimeMultiplier = 1f;
        int _carryEndedFrame = -10;

        /// <summary>Holding a body or prop with telekinesis.</summary>
        public bool IsCarrying => _carrying;

        /// <summary>The speed the character moves at while carrying, 1 when not carrying.</summary>
        public float CarrySpeedMultiplier => _carrySpeed;

        /// <summary>Telekinesis has the interact key (a body is targeted).</summary>
        public bool IsInteractClaimed => _interactClaimed;

        /// <summary>
        /// Holding a body or prop: the character moves slower, and every input that shares a key with
        /// the throw is muted.
        /// </summary>
        public void SetCarrying(bool carrying, float speedMultiplier)
        {
            if (_carrying && !carrying)
                _carryEndedFrame = Time.frameCount;

            _carrying = carrying;
            _carrySpeed = carrying ? Mathf.Clamp(speedMultiplier, 0.05f, 1f) : 1f;
            ApplyCarrySlowdown();

            var locks = Locks;
            if (locks == null)
                return;
            if (carrying)
                locks.Mute(_carryKeys, CarryBlockedInputs);
            else
                locks.Unmute(_carryKeys, CarryBlockedInputs);
        }

        /// <summary>
        /// While a body is targeted but not yet held, the interact key belongs to telekinesis:
        /// otherwise pressing E at a body lying beside a door would lift the body and use the door.
        /// </summary>
        public void SetInteractClaimed(bool claimed)
        {
            if (_interactClaimed == claimed)
                return;
            _interactClaimed = claimed;

            var locks = Locks;
            if (locks == null)
                return;
            if (claimed)
                locks.Mute(_interactKey, InteractInput);
            else
                locks.Unmute(_interactKey, InteractInput);
        }

        /// <summary>
        /// Malbers' time multiplier scales its root-motion movement and the animation driving it
        /// together, so the character walks slower rather than sliding.
        /// </summary>
        void ApplyCarrySlowdown()
        {
            var animal = Animal;
            if (animal == null)
                return;

            bool slow = _carrying && _carrySpeed < 0.999f;
            if (slow)
            {
                if (!_timeScaled)
                {
                    _savedTimeMultiplier = animal.TimeMultiplier;
                    _timeScaled = true;
                }

                animal.TimeMultiplier = _savedTimeMultiplier * _carrySpeed;
            }
            else if (_timeScaled)
            {
                animal.TimeMultiplier = _savedTimeMultiplier;
                _timeScaled = false;
            }
        }

        /// <summary>
        /// True in the frame carrying ended and the one after: the click that throws must not also
        /// swing the sword, whichever of the two reads the mouse first.
        /// </summary>
        bool JustStoppedCarrying => Time.frameCount - _carryEndedFrame <= 1;
    }
}
