using TMPro;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// The collar warning, top centre, escalating on the same thresholds as the team panel:
    /// a quiet yellow line at 20 m, STAY IN RANGE! at 25 m, TOO FAR APART! with the regroup timer
    /// once the collar's length is passed, and YANKED BACK! when the timer runs out.
    ///
    /// Everything else on the HUD stays in the corners; this and critical health are the only things
    /// allowed into the middle of the screen, because this is the game's core rule.
    /// </summary>
    public class CollarWarningView : MonoBehaviour
    {
        CanvasGroup _group;
        TextMeshProUGUI _title;
        RectTransform _lineRow;
        IconStack _chain;
        TextMeshProUGUI _line;
        RectTransform _timerRow;
        TextMeshProUGUI _timer;
        SketchBar _timerBar;
        TextMeshProUGUI _subtitle;

        CollarStage _shownStage = (CollarStage)(-1);
        string _shownWho;
        int _shownMetres = -1;
        int _shownTenths = -1;

        public static CollarWarningView Create(Transform parent)
        {
            var column = UiKit.CreateColumn(parent, "CollarWarning", 8f, TextAnchor.UpperCenter, fitToContent: false);
            column.Place(new Vector2(0.5f, 1f), new Vector2(0f, -128f), new Vector2(900f, 260f));

            var view = column.gameObject.AddComponent<CollarWarningView>();
            view._group = column.gameObject.AddComponent<CanvasGroup>();
            view.Build(column);
            return view;
        }

        void Build(RectTransform column)
        {
            var theme = UiTheme.Active;

            _title = UiKit.CreateText(column, "Title", string.Empty,
                TextStyle.Heading.WithAlign(TextAlignmentOptions.Center));

            _lineRow = UiKit.CreateRow(column, "Line", 10f, TextAnchor.MiddleCenter, fitToContent: false);
            _chain = UiKit.CreateIcon(_lineRow, "Chain", 34f, UiSprites.ChainInk, UiSprites.ChainLine, theme.warning);
            _line = UiKit.CreateText(_lineRow, "Text", string.Empty, TextStyle.Number.WithSize(32f));

            _timerRow = UiKit.CreateRow(column, "Timer", 14f, TextAnchor.MiddleCenter, fitToContent: false);
            _timer = UiKit.CreateText(_timerRow, "Text", string.Empty, TextStyle.Prompt.WithTint(ColorRole.Danger));
            _timerBar = SketchBar.Create(_timerRow, "Left", new Vector2(300f, 20f), theme.danger, shine: false);
            _timerBar.SetTrackColor(new Color(0f, 0f, 0f, 0.6f));

            _subtitle = UiKit.CreateText(column, "Subtitle", string.Empty,
                TextStyle.Body.WithAlign(TextAlignmentOptions.Center));
        }

        public void Set(in CollarData data)
        {
            bool visible = data.Stage != CollarStage.None;
            _group.alpha = visible ? 1f : 0f;
            if (!visible)
            {
                _shownStage = CollarStage.None;
                return;
            }

            var theme = UiTheme.Active;
            if (_shownStage != data.Stage)
            {
                _shownStage = data.Stage;
                _shownWho = null;
                _shownMetres = -1;
                _shownTenths = -1;

                switch (data.Stage)
                {
                    case CollarStage.Danger:
                        Title("STAY IN RANGE!", 66f, -2f);
                        break;
                    case CollarStage.Breach:
                        Title("TOO FAR APART!", 84f, -3f);
                        break;
                    case CollarStage.Failed:
                        Title("YANKED BACK!", 84f, -3f);
                        break;
                    default:
                        Title(null, 0f, 0f);
                        break;
                }

                Show(_lineRow, data.Stage == CollarStage.Warning || data.Stage == CollarStage.Danger);
                Show(_timerRow, data.Stage == CollarStage.Breach);
                Show(_subtitle, data.Stage == CollarStage.Failed);
                _subtitle.text = data.Stage == CollarStage.Failed
                    ? "The collar snapped. Everyone goes back to the last checkpoint."
                    : string.Empty;

                var tint = data.Stage == CollarStage.Warning ? theme.warning : theme.danger;
                _chain.SetTint(tint);
                _line.color = tint;
            }

            int metres = Mathf.RoundToInt(data.Metres);
            if ((data.Stage == CollarStage.Warning || data.Stage == CollarStage.Danger) &&
                (metres != _shownMetres || data.Who != _shownWho))
            {
                _shownMetres = metres;
                _shownWho = data.Who;
                _line.text = data.Stage == CollarStage.Warning
                    ? data.Who + " is drifting away · " + metres + " m"
                    : data.Who + " is " + metres + " m away. Get closer!";
            }

            if (data.Stage == CollarStage.Breach)
            {
                int tenths = Mathf.CeilToInt(Mathf.Max(0f, data.SecondsLeft) * 10f);
                if (tenths != _shownTenths)
                {
                    _shownTenths = tenths;
                    _timer.SetText("Regroup in {0:1} s", tenths / 10f);
                }
                _timerBar.SetValue(data.Left01);
            }
        }

        void Title(string text, float size, float tilt)
        {
            Show(_title, !string.IsNullOrEmpty(text));
            if (string.IsNullOrEmpty(text))
                return;

            _title.fontSize = size;
            _title.text = text;
            _title.rectTransform.Tilt(tilt);
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
