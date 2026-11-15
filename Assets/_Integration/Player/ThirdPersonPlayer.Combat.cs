using CollarCali.Combat;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// The sword: Game Creator 2 Melee through MAnimalGC2Bridge. The bridge's rules decide whether an
    /// attack may start (on the ground, in Malbers' Idle or Locomotion, with no Malbers mode playing)
    /// and cancel one when they stop holding. This part decides whether the attack button is the
    /// sword's at all: not while downed, in a menu, carrying, or aiming a throw.
    /// </summary>
    public partial class ThirdPersonPlayer
    {
        [Header("Combat")]
        [Tooltip("The melee weapon equipped when play starts: Combat/GC2/Content/Sword/TeamSword_Weapon. Empty: none.")]
        [SerializeField] MeleeWeapon startingWeapon;

        MAnimalGC2Bridge _melee;
        float _nextHitboxScan;

        /// <summary>The melee bridge on this character, when it has Game Creator melee.</summary>
        public MAnimalGC2Bridge MeleeBridge => _melee;

        /// <summary>Why the last press of the attack button did not swing, or null if it did.</summary>
        public string LastAttackRefusal { get; private set; }

        void StartCombat()
        {
            _melee = GetComponent<MAnimalGC2Bridge>();
            if (_melee == null || _melee.Character == null || startingWeapon == null)
                return;
            _ = _melee.Character.Combat.Equip(startingWeapon, null, new Args(gameObject));
        }

        void UpdateCombat()
        {
            if (_melee == null || !_running)
                return;

            if (Time.unscaledTime >= _nextHitboxScan)
            {
                _nextHitboxScan = Time.unscaledTime + 0.5f;
                MeleeHitboxMarker.MarkZombies();
            }

            if (AttackPressed())
                TryAttack();
        }

        /// <summary>
        /// A press of the attack button: swings when the button is the sword's and the melee rules
        /// allow it. A press mid-swing is buffered for the next hit of the combo.
        /// </summary>
        public bool TryAttack()
        {
            if (_melee == null)
            {
                LastAttackRefusal = "no melee";
                return false;
            }

            if (!AttackButtonIsSwords(out string reason))
            {
                LastAttackRefusal = reason;
                return false;
            }

            if (!_melee.TryAttack(MeleeKey.A))
            {
                LastAttackRefusal = _melee.LastRefusal;
                return false;
            }

            LastAttackRefusal = null;
            return true;
        }

        /// <summary>
        /// Whether the attack button belongs to the sword right now. <paramref name="reason"/> says
        /// what has it otherwise.
        /// </summary>
        public bool AttackButtonIsSwords(out string reason)
        {
            var telekinesis = BodyTelekinesis.Local;
            reason = _suspended ? "downed"
                : _menuOpen ? "a menu is open"
                : _carrying || JustStoppedCarrying ? "carrying"
                : _aimViewRequested || _aimEyeLive ? "aiming a throw"
                : telekinesis != null && telekinesis.isActiveAndEnabled && telekinesis.IsHolding ? "telekinesis is holding something"
                : null;
            return reason == null;
        }

        static bool AttackPressed() =>
            (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) ||
            (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame);

        void CancelMelee(string reason)
        {
            if (_melee != null)
                _melee.CancelAttack(reason);
        }
    }
}
