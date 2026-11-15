using System.Collections.Generic;
using MalbersAnimations.Controller;
using MalbersAnimations.InputSystem;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Shares a Malbers character's locks and muted inputs between everything that needs them - a
    /// melee swing, the pause menu, carrying a body, being downed, a creep's grab - so one releasing
    /// its lock never frees the character while another still holds it.
    ///
    /// Each holder names itself (any object) and says what it holds: Malbers' movement lock, its input
    /// lock (jumps, actions, modes), and inputs of the character's MInputLink muted by name (such as
    /// Interact while telekinesis owns the E key). A lock is on while anyone holds it; an input is
    /// muted while anyone mutes it. Release(holder) gives back everything that holder took.
    ///
    /// Malbers' own flags are only switched off from here if they were switched on from here, so a
    /// lock Malbers already held itself is left to Malbers. While anyone holds a lock it is kept on,
    /// even if one of Malbers' own moves lets go of it meanwhile. Code that still writes LockMovement
    /// or LockInput directly (the old DualPlayerController) is not counted, so characters it runs
    /// should not use this as well.
    ///
    /// A muted input stays muted through a restart of the input link. If the link switches to another
    /// action map, call Reassert so the new map's buttons are muted too.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public class MAnimalLocks : MonoBehaviour
    {
        readonly HashSet<object> _movement = new HashSet<object>();
        readonly HashSet<object> _input = new HashSet<object>();
        readonly Dictionary<string, HashSet<object>> _muted = new Dictionary<string, HashSet<object>>();

        MAnimal _animal;
        MInputLink _link;
        bool _movementTaken, _inputTaken; // switched on from here, so switched off from here

        /// <summary>The lock set of <paramref name="animal"/>, added to it on first use.</summary>
        public static MAnimalLocks Of(MAnimal animal)
        {
            if (animal == null)
                return null;
            var locks = animal.GetComponent<MAnimalLocks>();
            return locks != null ? locks : animal.gameObject.AddComponent<MAnimalLocks>();
        }

        void Awake()
        {
            _animal = GetComponent<MAnimal>();
            _link = GetComponent<MInputLink>();
        }

        /// <summary>True while anyone holds the movement lock.</summary>
        public bool MovementLocked => _movement.Count > 0;

        /// <summary>True while anyone holds the input lock.</summary>
        public bool InputLocked => _input.Count > 0;

        /// <summary>Whether <paramref name="holder"/> holds anything here.</summary>
        public bool Holds(object holder) =>
            _movement.Contains(holder) || _input.Contains(holder) || IsMuting(holder);

        /// <summary><paramref name="holder"/> locks movement, input, or both, until it releases them.</summary>
        public void Lock(object holder, bool movement, bool input)
        {
            if (holder == null)
                return;
            if (movement)
                _movement.Add(holder);
            else
                _movement.Remove(holder);
            if (input)
                _input.Add(holder);
            else
                _input.Remove(holder);
            Apply();
        }

        /// <summary><paramref name="holder"/> mutes the character's inputs named <paramref name="inputs"/> (comma separated).</summary>
        public void Mute(object holder, string inputs)
        {
            if (holder == null || string.IsNullOrEmpty(inputs))
                return;
            foreach (string raw in inputs.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                    continue;
                if (!_muted.TryGetValue(name, out var holders))
                    _muted[name] = holders = new HashSet<object>();
                if (holders.Add(holder) && holders.Count == 1)
                    SetInput(name, false);
            }
        }

        /// <summary><paramref name="holder"/> stops muting <paramref name="inputs"/> (comma separated).</summary>
        public void Unmute(object holder, string inputs)
        {
            if (holder == null || string.IsNullOrEmpty(inputs))
                return;
            foreach (string raw in inputs.Split(','))
            {
                string name = raw.Trim();
                if (_muted.TryGetValue(name, out var holders) && holders.Remove(holder) && holders.Count == 0)
                    SetInput(name, true);
            }
        }

        /// <summary>Gives back everything <paramref name="holder"/> took: its locks and its muted inputs.</summary>
        public void Release(object holder)
        {
            if (holder == null)
                return;
            _movement.Remove(holder);
            _input.Remove(holder);
            foreach (var entry in _muted)
            {
                if (entry.Value.Remove(holder) && entry.Value.Count == 0)
                    SetInput(entry.Key, true);
            }

            Apply();
        }

        /// <summary>Mutes every muted input again and re-applies the locks, after the input link changed maps.</summary>
        public void Reassert()
        {
            foreach (var entry in _muted)
            {
                if (entry.Value.Count > 0)
                    SetInput(entry.Key, false);
            }

            Apply();
        }

        /// <summary>Whether anyone mutes <paramref name="input"/>.</summary>
        public bool IsMuted(string input) => input != null && _muted.TryGetValue(input.Trim(), out var holders) && holders.Count > 0;

        bool IsMuting(object holder)
        {
            foreach (var holders in _muted.Values)
            {
                if (holders.Contains(holder))
                    return true;
            }

            return false;
        }

        void Apply()
        {
            if (_animal == null)
                _animal = GetComponent<MAnimal>();
            if (_animal == null)
                return;

            if (_movement.Count > 0)
            {
                if (!_animal.LockMovement)
                {
                    _animal.LockMovement = true;
                    _movementTaken = true;
                }
            }
            else if (_movementTaken)
            {
                _movementTaken = false;
                _animal.LockMovement = false;
            }

            if (_input.Count > 0)
            {
                if (!_animal.LockInput)
                {
                    _animal.LockInput = true;
                    _inputTaken = true;
                }
            }
            else if (_inputTaken)
            {
                _inputTaken = false;
                _animal.LockInput = false;
            }
        }

        // A lock held here stays on even when one of Malbers' own moves releases it underneath.
        void Update()
        {
            if (_movement.Count > 0 || _input.Count > 0)
                Apply();
        }

        void SetInput(string name, bool enabled)
        {
            if (_link == null)
                _link = GetComponent<MInputLink>();
            if (_link != null)
                _link.EnableInput(name, enabled);
        }

        void OnDestroy()
        {
            _movement.Clear();
            _input.Clear();
            _muted.Clear();
        }
    }
}
