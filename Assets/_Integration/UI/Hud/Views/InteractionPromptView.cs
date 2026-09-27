using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The prompt for whatever is under the crosshair - "Pick up MP7", "Revive DOT" - in the same
    /// place and style as the carry prompts: a key cap, the action in Coming Soon, and under it a
    /// "hold E" hint that fills while the key is held.
    ///
    /// A blocked interaction (a revive station with no body, an attachment that does not fit) greys
    /// the key out and slashes it, and says why, instead of Cowsins' generic "Not Compatible".
    /// </summary>
    public class InteractionPromptView : MonoBehaviour
    {
        CanvasGroup _group;
        KeyCap _key;
        Image _slash;
        TextMeshProUGUI _label;
        RectTransform _hintRow;
        SketchBar _progress;
        TextMeshProUGUI _hint;

        string _shownText;
        string _shownHint;
        string _shownKey;
        bool _shownBlocked;

        public static InteractionPromptView Create(Transform parent)
        {
            var row = UiKit.CreateRow(parent, "InteractionPrompt", 16f, TextAnchor.MiddleCenter);
            // Centre of the prompt row on the boards: top 724 of 1080, the same line the carry prompts use.
            row.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -211f), new Vector2(600f, 80f));

            var view = row.gameObject.AddComponent<InteractionPromptView>();
            view._group = row.gameObject.AddComponent<CanvasGroup>();
            view.Build(row);
            return view;
        }

        void Build(RectTransform row)
        {
            _key = KeyCap.Create(row, "E", 54f);

            _slash = UiKit.CreateImage(_key.transform, "Slash", UiSprites.White, UiTheme.Active.danger);
            _slash.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4f, 62f));
            _slash.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -40f);

            var column = UiKit.CreateColumn(row, "Text", 6f, TextAnchor.MiddleLeft, fitToContent: false);
            _label = UiKit.CreateText(column, "Action", string.Empty, TextStyle.Prompt);

            _hintRow = UiKit.CreateRow(column, "Hint", 12f, TextAnchor.MiddleLeft, fitToContent: false);
            _progress = SketchBar.Create(_hintRow, "Progress", new Vector2(200f, 18f), UiTheme.Active.health, shine: false);
            _progress.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));
            _hint = UiKit.CreateText(_hintRow, "Text", string.Empty, TextStyle.Small.WithSize(22f));
        }

        public void Set(in PromptData data)
        {
            _group.alpha = data.Visible ? 1f : 0f;
            if (!data.Visible)
                return;

            var theme = UiTheme.Active;

            string key = string.IsNullOrEmpty(data.Key) ? "E" : data.Key;
            if (key != _shownKey || data.Blocked != _shownBlocked)
            {
                _shownKey = key;
                _shownBlocked = data.Blocked;
                _key.Set(key, !data.Blocked);
            }
            Show(_slash, data.Blocked);

            if (_shownText != data.Text)
            {
                _shownText = data.Text;
                _label.text = data.Text ?? string.Empty;
            }
            _label.color = data.Blocked ? theme.muted : theme.white;

            string hint = data.Hint;
            if (string.IsNullOrEmpty(hint) && data.Hold && !data.Blocked)
                hint = "hold " + key;

            bool showProgress = data.Hold && !data.Blocked && data.Progress01 > 0f;
            Show(_hintRow, !string.IsNullOrEmpty(hint) || showProgress);
            Show(_progress, showProgress);
            if (showProgress)
                _progress.SetValue(data.Progress01);

            if (_shownHint != hint)
            {
                _shownHint = hint;
                _hint.text = hint ?? string.Empty;
            }
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
