using MalbersAnimations.Events;
using UnityEngine.Events;

namespace CollarCali.UI
{
    /// <summary>
    /// The third-person climbing prompts, taken from the same Malbers events that drove Malbers' own
    /// "Interact UI" and "Ledge UI" (now hidden with the rest of its canvas): Steve's climb and ladder
    /// hits raise the interact event, a grabbable ledge raises the ledge one - each with true/false
    /// for show/hide and a word ("Climb"). They are drawn on the HUD's own prompt line, with the key
    /// the player has bound (the interact key, and jump for a ledge).
    /// </summary>
    public sealed class MalbersPromptSource
    {
        MEvent _interact;
        MEvent _ledge;
        MEventItemListener _interactListener;
        MEventItemListener _ledgeListener;
        bool _interactOn;
        bool _ledgeOn;
        string _interactText;
        string _ledgeText;

        public bool IsBound => _interact != null || _ledge != null;

        public void Bind(MEvent interact, MEvent ledge)
        {
            Unbind();
            _interact = interact;
            _ledge = ledge;
            if (_interact != null)
                _interactListener = Listen(_interact, on => _interactOn = on, text => _interactText = text);
            if (_ledge != null)
                _ledgeListener = Listen(_ledge, on => _ledgeOn = on, text => _ledgeText = text);
        }

        static MEventItemListener Listen(MEvent evt, UnityAction<bool> onShow, UnityAction<string> onText)
        {
            var listener = new MEventItemListener
            {
                Event = evt,
                useVoid = false,
                useBool = true,
                useString = true,
            };
            listener.ResponseBool.AddListener(onShow);
            listener.ResponseString.AddListener(onText);
            evt.RegisterListener(listener);
            return listener;
        }

        public void Unbind()
        {
            if (_interact != null && _interactListener != null)
                _interact.UnregisterListener(_interactListener);
            if (_ledge != null && _ledgeListener != null)
                _ledge.UnregisterListener(_ledgeListener);

            _interact = _ledge = null;
            _interactListener = _ledgeListener = null;
            _interactOn = _ledgeOn = false;
        }

        /// <summary>The prompt to show now; a ledge wins over a climb, as it is the more urgent.</summary>
        public PromptData Read()
        {
            if (_ledgeOn)
                return Prompt("Jumping", _ledgeText, "Climb up");
            if (_interactOn)
                return Prompt("Interacting", _interactText, "Climb");
            return default;
        }

        static PromptData Prompt(string action, string text, string fallback)
        {
            return new PromptData
            {
                Visible = true,
                Key = GameSettings.KeyLabel(action),
                Text = string.IsNullOrEmpty(text) ? fallback : text,
            };
        }
    }
}
