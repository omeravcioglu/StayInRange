using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    public enum CompassMark
    {
        Teammate,
        Station,
        Checkpoint,
    }

    /// <summary>Something the compass points at, by its bearing from north in degrees.</summary>
    public struct CompassMarker
    {
        public float Bearing;
        public CompassMark Kind;
        public Color Colour;

        /// <summary>A teammate past the collar's danger distance: their dot gets a red ring.</summary>
        public bool TooFar;
    }

    /// <summary>
    /// The compass along the top of the screen (HUD sheet): 90 degrees either side of where you
    /// face, ticks every 15, the cardinal letters with N in red, and pips for teammates (their
    /// colour), the next save pad (a flag) and - while someone is down - the revive station (a green
    /// cross). Anything behind you sticks to the nearer end with a little arrow.
    ///
    /// Everything is pooled and only moved each frame, so the HUD pays nothing per marker.
    /// </summary>
    public class CompassView : MonoBehaviour
    {
        const float Width = 840f;
        const float Height = 112f;
        const float Centre = 420f;
        const float PixelsPerDegree = 400f / 90f;
        const float Clamp = 86f;
        const int MaxTicks = 13;
        const int MaxLabels = 5;
        const int MaxPips = 8;

        static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        static readonly Color North = UiTheme.Rgb(0xFF5A52);
        static readonly Color Alarm = UiTheme.Rgb(0xFF5A52);

        RectTransform _root;
        TextMeshProUGUI _heading;
        readonly Tick[] _ticks = new Tick[MaxTicks];
        readonly TextMeshProUGUI[] _labels = new TextMeshProUGUI[MaxLabels];
        readonly Pip[] _pips = new Pip[MaxPips];
        int _shownHeading = -1;

        struct Tick
        {
            public RectTransform Ink;
            public RectTransform Line;
            public Image LineImage;
        }

        struct Pip
        {
            public RectTransform Root;
            public Image Ring;
            public Image Fill;
            public Image Station;
            public Image Flag;
            public IconStack Left;
            public IconStack Right;
        }

        public static CompassView Create(Transform parent)
        {
            var rect = UiKit.CreateRect("Compass", parent);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -12f);
            rect.sizeDelta = new Vector2(Width, Height);

            var view = rect.gameObject.AddComponent<CompassView>();
            view.Build(rect);
            view.Set(0f, null);
            return view;
        }

        void Build(RectTransform root)
        {
            _root = root;
            var theme = UiTheme.Active;

            _heading = UiKit.CreateText(root, "Heading", "000",
                TextStyle.Body.WithSize(24f).WithTint(ColorRole.White).WithAlign(TextAlignmentOptions.Center));
            At(_heading.rectTransform, 370f, 0f, 100f, 28f);

            // The rule: an ink stroke with the cream line on it.
            var ink = UiKit.CreateImage(root, "Rule Ink", UiSprites.SketchLine, theme.ink);
            At(ink.rectTransform, 16f, 49f, 808f, 24f);
            var line = UiKit.CreateImage(root, "Rule", UiSprites.SketchLine, theme.cream);
            At(line.rectTransform, 20f, 55f, 800f, 12f);

            for (int i = 0; i < MaxTicks; i++)
            {
                var tickInk = UiKit.CreateImage(root, "Tick Ink", UiSprites.White, theme.ink);
                var tickLine = UiKit.CreateImage(root, "Tick", UiSprites.White, theme.cream);
                _ticks[i] = new Tick { Ink = tickInk.rectTransform, Line = tickLine.rectTransform, LineImage = tickLine };
            }

            for (int i = 0; i < MaxLabels; i++)
            {
                _labels[i] = UiKit.CreateText(root, "Label", string.Empty,
                    TextStyle.Body.WithSize(32f).WithAlign(TextAlignmentOptions.Center));
                _labels[i].rectTransform.sizeDelta = new Vector2(60f, 34f);
            }

            var pointer = UiKit.CreateImage(root, "Pointer", UiSprites.Tick, theme.cream);
            At(pointer.rectTransform, 408f, 30f, 24f, 16f);

            for (int i = 0; i < MaxPips; i++)
                _pips[i] = CreatePip(root);
        }

        Pip CreatePip(RectTransform root)
        {
            var theme = UiTheme.Active;
            var holder = UiKit.CreateRect("Pip", root);
            holder.sizeDelta = new Vector2(36f, 32f);
            holder.anchorMin = holder.anchorMax = holder.pivot = new Vector2(0f, 1f);

            var pip = new Pip { Root = holder };
            pip.Ring = UiKit.CreateImage(holder, "Ring", UiSprites.Circle, theme.ink);
            pip.Ring.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
            pip.Fill = UiKit.CreateImage(holder, "Fill", UiSprites.Circle, Color.white);
            pip.Fill.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16f, 16f));
            pip.Station = UiKit.CreateImage(holder, "Station", UiSprites.Cross, Color.white);
            pip.Station.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26f, 26f));
            pip.Flag = UiKit.CreateImage(holder, "Flag", UiSprites.Flag, Color.white);
            pip.Flag.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26f, 28f));

            pip.Left = UiKit.CreateIcon(holder, "Behind Left", 20f, UiSprites.ChevronLeftInk, UiSprites.ChevronLeftLine, theme.cream);
            ((RectTransform)pip.Left.transform).Place(new Vector2(0f, 0.5f), new Vector2(-8f, 0f), new Vector2(12f, 20f));
            pip.Right = UiKit.CreateIcon(holder, "Behind Right", 20f, UiSprites.ChevronRightInk, UiSprites.ChevronRightLine, theme.cream);
            ((RectTransform)pip.Right.transform).Place(new Vector2(1f, 0.5f), new Vector2(8f, 0f), new Vector2(12f, 20f));

            holder.gameObject.SetActive(false);
            return pip;
        }

        /// <summary>Pins a child by the board's corner coordinates inside the 840x112 compass.</summary>
        static void At(RectTransform rect, float left, float top, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(left, -top);
            rect.sizeDelta = new Vector2(width, height);
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        /// <summary><paramref name="heading"/> is where the camera faces, in degrees from north.</summary>
        public void Set(float heading, IReadOnlyList<CompassMarker> markers)
        {
            float h = Mathf.Repeat(heading, 360f);
            int rounded = Mathf.RoundToInt(h) % 360;
            if (rounded != _shownHeading)
            {
                _shownHeading = rounded;
                _heading.SetText("{0:000}", rounded);
            }

            LayoutTicks(h);
            LayoutPips(h, markers);
        }

        void LayoutTicks(float heading)
        {
            int tick = 0;
            int label = 0;
            float first = Mathf.Ceil((heading - 90f) / 15f) * 15f;
            for (float a = first; a <= heading + 90f && tick < MaxTicks; a += 15f)
            {
                float x = Centre + (a - heading) * PixelsPerDegree;
                int norm = Mathf.RoundToInt(Mathf.Repeat(a, 360f)) % 360;
                bool cardinal = norm % 90 == 0;
                bool inter = !cardinal && norm % 45 == 0;
                float top = cardinal ? 49f : inter ? 52f : 55f;
                float height = cardinal ? 24f : inter ? 18f : 12f;

                var t = _ticks[tick++];
                t.Ink.gameObject.SetActive(true);
                t.Line.gameObject.SetActive(true);
                At(t.Ink, x - 3f, top - 1.5f, 6f, height + 3f);
                At(t.Line, x - 1.5f, top, 3f, height);
                var colour = t.LineImage.color;
                colour.a = cardinal ? 1f : 0.75f;
                t.LineImage.color = colour;

                if (norm % 45 == 0 && Mathf.Abs(a - heading) <= 84f && label < MaxLabels)
                {
                    var text = _labels[label++];
                    text.gameObject.SetActive(true);
                    text.text = Cardinals[norm / 45];
                    text.fontSize = cardinal ? 32f : 22f;
                    text.color = norm == 0 ? North : UiTheme.Active.cream;
                    At(text.rectTransform, x - 30f, 74f, 60f, 34f);
                }
            }

            for (int i = tick; i < MaxTicks; i++)
            {
                _ticks[i].Ink.gameObject.SetActive(false);
                _ticks[i].Line.gameObject.SetActive(false);
            }

            for (int i = label; i < MaxLabels; i++)
                _labels[i].gameObject.SetActive(false);
        }

        void LayoutPips(float heading, IReadOnlyList<CompassMarker> markers)
        {
            int count = markers != null ? Mathf.Min(markers.Count, MaxPips) : 0;
            for (int i = 0; i < MaxPips; i++)
            {
                var pip = _pips[i];
                if (i >= count)
                {
                    if (pip.Root.gameObject.activeSelf)
                        pip.Root.gameObject.SetActive(false);
                    continue;
                }

                var marker = markers[i];
                float delta = Mathf.DeltaAngle(heading, marker.Bearing);
                float clamped = Mathf.Clamp(delta, -Clamp, Clamp);
                pip.Root.gameObject.SetActive(true);
                pip.Root.anchoredPosition = new Vector2(Centre + clamped * PixelsPerDegree - 18f, -12f);

                bool dot = marker.Kind == CompassMark.Teammate;
                pip.Ring.gameObject.SetActive(dot);
                pip.Fill.gameObject.SetActive(dot);
                pip.Station.gameObject.SetActive(marker.Kind == CompassMark.Station);
                pip.Flag.gameObject.SetActive(marker.Kind == CompassMark.Checkpoint);
                if (dot)
                {
                    pip.Fill.color = marker.Colour;
                    pip.Ring.color = marker.TooFar ? Alarm : UiTheme.Active.ink;
                }

                pip.Left.gameObject.SetActive(delta < -Clamp);
                pip.Right.gameObject.SetActive(delta > Clamp);
            }
        }
    }
}
