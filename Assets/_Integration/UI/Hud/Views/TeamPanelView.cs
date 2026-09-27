using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The team panel: one row per player, top left, in the same order on every machine.
    ///
    /// It is the brief's most important readout - where is everyone, how far, how hurt - so each
    /// row carries the player's colour, their name, a health bar that turns into DOWN / CARRIED when
    /// they die, and on the right their distance in the collar's colours (your own row shows your
    /// health instead).
    /// </summary>
    public class TeamPanelView : MonoBehaviour
    {
        const float RowHeight = 84f;
        const float RowGap = 6f;

        readonly List<TeamRowView> _rows = new List<TeamRowView>();
        RectTransform _rect;

        public static TeamPanelView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("TeamPanel", parent);
            rect.Place(new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(TeamRowView.Width, 4f * (RowHeight + RowGap)));
            var view = rect.gameObject.AddComponent<TeamPanelView>();
            view._rect = rect;
            return view;
        }

        public void Set(IReadOnlyList<TeamRowData> rows)
        {
            while (_rows.Count < rows.Count)
            {
                var row = TeamRowView.Create(_rect, _rows.Count);
                ((RectTransform)row.transform).Place(new Vector2(0f, 1f),
                    new Vector2(0f, -_rows.Count * (RowHeight + RowGap)), new Vector2(TeamRowView.Width, RowHeight));
                _rows.Add(row);
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < rows.Count;
                if (_rows[i].gameObject.activeSelf != used)
                    _rows[i].gameObject.SetActive(used);
                if (used)
                    _rows[i].Set(rows[i]);
            }
        }
    }

    /// <summary>One player: swatch, name line, bar, distance or your health, voice.</summary>
    public class TeamRowView : MonoBehaviour
    {
        public const float Width = 470f;

        IconStack _swatch;
        Image _skull;
        IconStack _carriedHand;

        RectTransform _nameLine;
        Image _eye;
        TextMeshProUGUI _name;
        TextMeshProUGUI _you;
        IconStack _carryingHand;
        TextMeshProUGUI _carrying;

        SketchBar _bar;
        Image _shield;
        TextMeshProUGUI _barText;

        IconStack _chain;
        TextMeshProUGUI _distance;
        IconStack _heart;
        TextMeshProUGUI _health;

        IconStack _voice;
        Image _mute;

        int _shownMetres = -1;
        int _shownHealth = -1;
        string _shownName;
        string _shownCarrying;
        string _shownCarrier;
        RowStatus _shownStatus = (RowStatus)(-1);

        public static TeamRowView Create(Transform parent, int index)
        {
            var rect = UiKit.CreateRect("Row " + index, parent);
            var row = rect.gameObject.AddComponent<TeamRowView>();
            row.Build(rect);
            return row;
        }

        void Build(RectTransform rect)
        {
            var theme = UiTheme.Active;

            // Swatch, with the DOWN skull or CARRIED hand drawn over it.
            _swatch = UiKit.CreateSwatch(rect, Color.white, 52f);
            ((RectTransform)_swatch.transform).Place(new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(52f, 52f));

            _skull = UiKit.CreateImage(_swatch.transform, "Down", UiSprites.Skull, Color.white);
            _skull.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
            _skull.preserveAspect = true;

            _carriedHand = UiKit.CreateIcon(_swatch.transform, "Carried", 34f, UiSprites.HandInk, UiSprites.HandLine, theme.warning);
            ((RectTransform)_carriedHand.transform).Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));

            // Middle column: the name line over the bar.
            _nameLine = UiKit.CreateRow(rect, "NameLine", 6f);
            _nameLine.Place(new Vector2(0f, 1f), new Vector2(72f, -8f), new Vector2(236f, 34f));

            _eye = UiKit.CreateImage(_nameLine, "Watching", UiSprites.Eye, Color.white);
            AddSize(_eye.gameObject, 26f, 26f);
            _eye.preserveAspect = true;

            _name = UiKit.CreateText(_nameLine, "Name", string.Empty, TextStyle.Name);
            _you = UiKit.CreateText(_nameLine, "You", "you", TextStyle.Small.WithSize(20f));

            _carryingHand = UiKit.CreateIcon(_nameLine, "Carrying", 22f, UiSprites.HandInk, UiSprites.HandLine, theme.warning);
            _carrying = UiKit.CreateText(_nameLine, "CarryingName", string.Empty,
                TextStyle.Small.WithSize(22f).WithTint(ColorRole.Warning));

            _bar = SketchBar.Create(rect, "Health", new Vector2(236f, 30f), theme.health);
            ((RectTransform)_bar.transform).Place(new Vector2(0f, 1f), new Vector2(72f, -46f), new Vector2(236f, 30f));

            // Shield: an icy strip along the top of the bar, only when a shield exists at all.
            _shield = UiKit.CreateImage(_bar.transform, "Shield", UiSprites.White, theme.shield);
            _shield.rectTransform.anchorMin = new Vector2(0f, 1f);
            _shield.rectTransform.anchorMax = new Vector2(0f, 1f);
            _shield.rectTransform.pivot = new Vector2(0f, 1f);
            _shield.rectTransform.anchoredPosition = new Vector2(5f, -5f);
            _shield.rectTransform.sizeDelta = new Vector2(0f, 7f);

            _barText = UiKit.CreateText(_bar.transform, "State", string.Empty,
                TextStyle.Small.WithSize(21f).WithTint(ColorRole.Danger).WithAlign(TextAlignmentOptions.Center));
            _barText.rectTransform.Fill();

            // Right column: distance for teammates, your health for you.
            _chain = UiKit.CreateIcon(rect, "Strain", 24f, UiSprites.ChainInk, UiSprites.ChainLine, theme.warning);
            ((RectTransform)_chain.transform).Place(new Vector2(0f, 0.5f), new Vector2(320f, 0f), new Vector2(24f, 24f));

            _distance = UiKit.CreateText(rect, "Distance", string.Empty, TextStyle.Number);
            _distance.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(346f, 0f), new Vector2(84f, 40f));

            _heart = IconStack.Create(rect, "Heart", new Vector2(28f, 28f), theme.heart,
                IconStack.TintLayer(UiSprites.HeartFill), IconStack.PlainLayer(UiSprites.HeartInk));
            ((RectTransform)_heart.transform).Place(new Vector2(0f, 0.5f), new Vector2(320f, 0f), new Vector2(28f, 28f));

            _health = UiKit.CreateText(rect, "HealthNumber", string.Empty, TextStyle.Number.WithSize(32f));
            _health.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(352f, 0f), new Vector2(76f, 40f));

            // Voice: speaker with waves, or the red cross when muted.
            _voice = IconStack.Create(rect, "Voice", new Vector2(34f, 32f), theme.cream,
                IconStack.PlainLayer(UiSprites.Speaker), IconStack.TintLayer(UiSprites.SpeakerWaves));
            ((RectTransform)_voice.transform).Place(new Vector2(0f, 0.5f), new Vector2(428f, 0f), new Vector2(34f, 32f));
            _mute = UiKit.CreateImage(_voice.transform, "Muted", UiSprites.SpeakerMute, Color.white);
            _mute.rectTransform.Fill();
            _mute.preserveAspect = true;
        }

        static void AddSize(GameObject go, float width, float height)
        {
            var element = go.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = height;
        }

        public void Set(in TeamRowData data)
        {
            var theme = UiTheme.Active;
            bool alive = data.Status == RowStatus.Alive;

            _swatch.SetTint(alive ? data.Colour : theme.dim);
            SetActive(_skull, data.Status == RowStatus.Down);
            SetActive(_carriedHand, data.Status == RowStatus.Carried);

            // Name line.
            SetActive(_eye, data.Watched);
            if (_shownName != data.Name)
            {
                _shownName = data.Name;
                _name.text = data.Name ?? string.Empty;
            }
            SetActive(_you, data.IsSelf);

            bool carrying = !string.IsNullOrEmpty(data.Carrying);
            SetActive(_carryingHand, carrying);
            SetActive(_carrying, carrying);
            if (carrying && _shownCarrying != data.Carrying)
            {
                _shownCarrying = data.Carrying;
                _carrying.text = data.Carrying;
            }

            // The bar: health while alive, the state word once down.
            float health01 = data.Health01;
            _bar.SetValue(alive ? health01 : 0f);
            if (alive)
                _bar.SetFillColor(health01 < 0.3f ? theme.danger : theme.health);

            if (data.Status != _shownStatus || (data.Status == RowStatus.Carried && data.CarriedBy != _shownCarrier))
            {
                _shownStatus = data.Status;
                _shownCarrier = data.CarriedBy;
                switch (data.Status)
                {
                    case RowStatus.Down:
                        _barText.characterSpacing = 4f;
                        _barText.color = theme.danger;
                        _barText.text = "DOWN";
                        break;
                    case RowStatus.Carried:
                        _barText.characterSpacing = 0f;
                        _barText.color = theme.warning;
                        _barText.text = string.IsNullOrEmpty(data.CarriedBy) ? "CARRIED" : "CARRIED BY " + data.CarriedBy;
                        break;
                    default:
                        _barText.text = string.Empty;
                        break;
                }
            }

            bool shield = alive && data.Shield01 > 0f;
            SetActive(_shield, shield);
            if (shield)
                _shield.rectTransform.sizeDelta = new Vector2(226f * Mathf.Clamp01(data.Shield01), 7f);

            // Right column.
            bool showHealth = data.IsSelf && alive;
            bool showDistance = !data.IsSelf;
            SetActive(_heart, showHealth);
            SetActive(_health, showHealth);
            SetActive(_distance, showDistance);

            if (showHealth)
            {
                bool low = health01 < 0.3f;
                _heart.SetTint(low ? theme.heartLow : theme.heart);
                _health.color = low ? theme.danger : theme.white;
                int hp = Mathf.CeilToInt(data.Health);
                if (hp != _shownHealth)
                {
                    _shownHealth = hp;
                    _health.SetText("{0}", hp);
                }
            }

            bool strained = false;
            if (showDistance)
            {
                var tether = theme.GetTetherColor(data.Metres);
                _distance.color = tether;
                int metres = Mathf.RoundToInt(data.Metres);
                if (metres != _shownMetres)
                {
                    _shownMetres = metres;
                    _distance.SetText("{0}m", metres);
                }

                strained = data.Metres >= UiTheme.TetherWarningMetres;
                if (strained)
                    _chain.SetTint(tether);
            }
            SetActive(_chain, strained);
            // The distance shifts right to make room for the chain, as on the board.
            _distance.rectTransform.anchoredPosition = new Vector2(strained ? 346f : 320f, 0f);

            // Voice.
            bool voice = data.Voice != VoiceState.Hidden;
            SetActive(_voice, voice);
            if (voice)
            {
                _voice.SetTint(data.Voice == VoiceState.Talking ? theme.safe
                    : new Color(theme.cream.r, theme.cream.g, theme.cream.b, data.Voice == VoiceState.Muted ? 0f : 0.4f));
                SetActive(_mute, data.Voice == VoiceState.Muted);
            }
        }

        static void SetActive(Component component, bool active)
        {
            if (component != null && component.gameObject.activeSelf != active)
                component.gameObject.SetActive(active);
        }
    }
}
