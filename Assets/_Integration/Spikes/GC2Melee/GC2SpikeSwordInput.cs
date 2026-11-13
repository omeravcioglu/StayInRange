using System.Collections;
using System.Collections.Generic;
using CollarCali.Combat;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali.Spikes
{
    /// <summary>
    /// Spike only, to try the team sword in play mode: equips it when play starts, swings it on the
    /// left mouse button (or the gamepad's west button), and takes those presses away from Malbers'
    /// own attack modes on this character, which otherwise answer the same click with fists. A press
    /// mid-swing is buffered for the next hit of the combo; the bridge's rules decide whether an
    /// attack may start. The phase 3 controller replaces this with the team's input.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class GC2SpikeSwordInput : MonoBehaviour
    {
        [Tooltip("The weapon to equip (Combat/GC2/Content/Sword/TeamSword_Weapon).")]
        [SerializeField] MeleeWeapon m_Weapon;

        /// <summary>Malbers' inputs this took from its attack modes, for the checks.</summary>
        public string TakenInputs { get; private set; } = "";

        MAnimalGC2Bridge _bridge;

        IEnumerator Start()
        {
            _bridge = GetComponent<MAnimalGC2Bridge>();
            if (_bridge == null || _bridge.Character == null || m_Weapon == null)
                yield break;

            yield return null; // Malbers' input link builds its button map when it starts
            TakeMalbersAttacks();
            _ = _bridge.Character.Combat.Equip(m_Weapon, null, new Args(gameObject));
        }

        void TakeMalbersAttacks()
        {
            var animal = _bridge.Animal;
            var link = GetComponent<MalbersAnimations.InputSystem.MInputLink>();
            if (animal == null || link == null)
                return;

            var taken = new List<string>();
            foreach (var mode in animal.modes)
            {
                if (mode?.ID == null || !mode.ID.name.Contains("Attack") || string.IsNullOrEmpty(mode.Input))
                    continue;
                link.DisableInput(mode.Input);
                taken.Add(mode.Input);
            }

            // The click reaches Malbers' fists through its combo manager, which plays nothing while off.
            foreach (var combos in GetComponentsInChildren<MalbersAnimations.Controller.ComboManager>(true))
            {
                if (!combos.enabled)
                    continue;
                combos.enabled = false;
                taken.Add("its fist combos (ComboManager)");
            }

            TakenInputs = string.Join(", ", taken);
        }

        void Update()
        {
            if (_bridge == null)
                return;

            bool pressed = (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) ||
                           (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame);
            if (pressed)
                _bridge.TryAttack(MeleeKey.A);

            if (Time.unscaledTime >= _nextZombieScan)
            {
                _nextZombieScan = Time.unscaledTime + 0.5f;
                OpenZombiesToMelee();
            }
        }

        float _nextZombieScan;

        /// <summary>
        /// Game Creator's melee only hurts colliders marked TeamCanHit. Until the enemy bootstrap
        /// (phase 4) marks them, this marks the trigger hitboxes of every zombie in the scene, the
        /// spawned ones too, so the sword can be tried on them.
        /// </summary>
        static void OpenZombiesToMelee()
        {
            foreach (var zombie in FindObjectsByType<ZombieHealth>(FindObjectsSortMode.None))
            {
                foreach (var hitbox in zombie.GetComponentsInChildren<Collider>(true))
                {
                    if (hitbox.isTrigger && hitbox.GetComponent<TeamCanHit>() == null)
                        hitbox.gameObject.AddComponent<TeamCanHit>();
                }
            }
        }
    }
}
