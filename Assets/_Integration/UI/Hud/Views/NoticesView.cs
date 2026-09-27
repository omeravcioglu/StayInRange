using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The quiet corner, top right: a save pad waiting for the team ("SAVE 2 · 3/4 on the pad ·
    /// waiting for ZIG"), the save itself, and the killfeed underneath. Small and out of the way on
    /// purpose - the design keeps the middle of the screen for the collar and critical health.
    /// </summary>
    public class NoticesView : MonoBehaviour
    {
        const int MaxFeed = 4;
        const float FeedSeconds = 5f;
        const float SavedSeconds = 2.5f;

        struct FeedEntry
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public float Until;
        }

        RectTransform _root;

        RectTransform _pending;
        TextMeshProUGUI _pendingTitle;
        TextMeshProUGUI _pendingCount;
        RectTransform _dots;
        readonly List<IconStack> _dotViews = new List<IconStack>();
        TextMeshProUGUI _waiting;

        RectTransform _saved;
        TextMeshProUGUI _savedTitle;
        float _savedUntil;

        readonly List<FeedEntry> _feed = new List<FeedEntry>();

        public static NoticesView Create(Transform parent)
        {
            var column = UiKit.CreateColumn(parent, "Notices", 4f, TextAnchor.UpperRight, fitToContent: false);
            column.Place(new Vector2(1f, 1f), new Vector2(-24f, -28f), new Vector2(420f, 420f));

            var view = column.gameObject.AddComponent<NoticesView>();
            view._root = column;
            view.Build(column);
            return view;
        }

        void Build(RectTransform column)
        {
            var theme = UiTheme.Active;

            // A pad waiting for the team.
            _pending = UiKit.CreateColumn(column, "Pending", 4f, TextAnchor.UpperRight, fitToContent: false);
            var titleRow = UiKit.CreateRow(_pending, "Title", 10f, TextAnchor.MiddleRight, fitToContent: false);
            var flag = UiKit.CreateImage(titleRow, "Flag", UiSprites.Flag, Color.white).Sized(34f, 36f);
            flag.preserveAspect = true;
            _pendingTitle = UiKit.CreateText(titleRow, "Save", string.Empty, TextStyle.Number);

            var countRow = UiKit.CreateRow(_pending, "Count", 8f, TextAnchor.MiddleRight, fitToContent: false);
            _pendingCount = UiKit.CreateText(countRow, "Text", string.Empty, TextStyle.Small.WithSize(26f).WithTint(ColorRole.Cream));
            _dots = UiKit.CreateRow(countRow, "Dots", 4f, TextAnchor.MiddleRight, fitToContent: false);

            _waiting = UiKit.CreateText(_pending, "Waiting", string.Empty, TextStyle.Small);

            // The save itself.
            _saved = UiKit.CreateColumn(column, "Saved", 2f, TextAnchor.UpperRight, fitToContent: false);
            _savedTitle = UiKit.CreateText(_saved, "Title", string.Empty, TextStyle.Number.WithTint(ColorRole.Safe));
            UiKit.CreateText(_saved, "Line", "the whole team made it", TextStyle.Small);

            Show(_pending, false);
            Show(_saved, false);
        }

        public void SetCheckpoint(in CheckpointData data)
        {
            Show(_pending, data.Pending);
            if (data.Pending)
            {
                _pendingTitle.SetText("SAVE {0}", data.Id);
                _pendingCount.SetText("{0}/{1} on the pad", data.Here, data.Needed);
                SetDots(data.Colours, data.Arrived);
                _waiting.text = string.IsNullOrEmpty(data.WaitingFor) ? string.Empty : "waiting for " + data.WaitingFor;
                Show(_waiting, !string.IsNullOrEmpty(data.WaitingFor));
            }

            if (data.JustSaved)
            {
                _savedTitle.SetText("CHECKPOINT {0} SAVED!", data.Id);
                _savedUntil = Time.unscaledTime + SavedSeconds;
            }
            Show(_saved, Time.unscaledTime < _savedUntil);
        }

        void SetDots(Color[] colours, bool[] arrived)
        {
            int count = colours != null ? colours.Length : 0;
            while (_dotViews.Count < count)
            {
                _dotViews.Add(IconStack.Create(_dots, "Dot", new Vector2(18f, 18f), Color.white,
                    IconStack.TintLayer(UiSprites.BlobFill), IconStack.PlainLayer(UiSprites.BlobInk)));
            }

            for (int i = 0; i < _dotViews.Count; i++)
            {
                bool used = i < count;
                Show(_dotViews[i], used);
                if (!used)
                    continue;

                // A player who is not on the pad yet is their colour faded to a ghost.
                var colour = colours[i];
                bool here = arrived != null && i < arrived.Length && arrived[i];
                colour.a = here ? 1f : 0.3f;
                _dotViews[i].SetTint(colour);
            }
        }

        /// <summary>"You [skull] Zombie" - who killed what.</summary>
        public void AddKill(string killer, string victim)
        {
            var row = UiKit.CreateRow(_root, "Kill", 8f, TextAnchor.MiddleRight, fitToContent: false);
            UiKit.CreateText(row, "Killer", killer, TextStyle.Small.WithSize(26f).WithTint(ColorRole.White));
            UiKit.CreateImage(row, "Skull", UiSprites.Skull, Color.white).Sized(22f, 22f).preserveAspect = true;
            UiKit.CreateText(row, "Victim", victim, TextStyle.Small.WithSize(26f).WithTint(ColorRole.Faint));
            AddFeed(row);
        }

        /// <summary>A plain feed line, e.g. "DOT is down" in red.</summary>
        public void AddLine(string text, ColorRole tint)
        {
            var row = UiKit.CreateRow(_root, "Line", 8f, TextAnchor.MiddleRight, fitToContent: false);
            UiKit.CreateText(row, "Text", text, TextStyle.Small.WithSize(26f).WithTint(tint));
            AddFeed(row);
        }

        void AddFeed(RectTransform row)
        {
            var entry = new FeedEntry
            {
                Root = row,
                Group = row.gameObject.AddComponent<CanvasGroup>(),
                Until = Time.unscaledTime + FeedSeconds,
            };
            _feed.Add(entry);

            while (_feed.Count > MaxFeed)
            {
                UiKit.DestroyObject(_feed[0].Root.gameObject);
                _feed.RemoveAt(0);
            }
        }

        void Update()
        {
            float now = Time.unscaledTime;
            for (int i = _feed.Count - 1; i >= 0; i--)
            {
                var entry = _feed[i];
                float left = entry.Until - now;
                if (left <= 0f)
                {
                    UiKit.DestroyObject(entry.Root.gameObject);
                    _feed.RemoveAt(i);
                    continue;
                }

                entry.Group.alpha = Mathf.Clamp01(left / 0.6f);
            }

            if (_saved.gameObject.activeSelf && now >= _savedUntil)
                Show(_saved, false);
        }

        static void Show(Component component, bool visible)
        {
            if (component != null && component.gameObject.activeSelf != visible)
                component.gameObject.SetActive(visible);
        }
    }
}
