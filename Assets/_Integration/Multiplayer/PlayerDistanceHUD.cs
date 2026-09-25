#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Local-only teammate distances. Does not restyle the Cowsins HUD.
    /// </summary>
    public class PlayerDistanceHUD : MonoBehaviour
    {
        struct Row
        {
            public RectTransform Root;
            public Image Swatch;
            public TextMeshProUGUI Label;
        }

        readonly List<Row> _rows = new();
        Canvas _canvas;
        RectTransform _list;
        FpsNetworkBridge _local;

        void Awake()
        {
            _local = GetComponent<FpsNetworkBridge>();
            BuildCanvas();
        }

        void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas.gameObject);
        }

        void LateUpdate()
        {
            if (_local == null || !_local.IsLocalOwner)
            {
                if (_canvas != null)
                    _canvas.enabled = false;
                return;
            }

            if (_canvas != null)
                _canvas.enabled = true;

            Refresh();
        }

        void BuildCanvas()
        {
            var go = new GameObject("PlayerDistanceHUD", typeof(RectTransform));
            go.transform.SetParent(null, false);
            DontDestroyOnLoad(go);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 25;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            go.AddComponent<GraphicRaycaster>().enabled = false;

            _list = new GameObject("List", typeof(RectTransform)).GetComponent<RectTransform>();
            _list.SetParent(go.transform, false);
            _list.anchorMin = new Vector2(1f, 1f);
            _list.anchorMax = new Vector2(1f, 1f);
            _list.pivot = new Vector2(1f, 1f);
            _list.anchoredPosition = new Vector2(-28f, -120f);
            _list.sizeDelta = new Vector2(280f, 240f);
        }

        void Refresh()
        {
            var others = new List<FpsNetworkBridge>();
            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                // Dead teammates stay on the list. Their collar still binds the team, so the distance
                // to a body is exactly as important as the distance to a living player - arguably
                // more, since somebody has to go and carry it.
                if (bridge == null || bridge == _local)
                    continue;
                if (bridge.Object == null || !bridge.Object.IsValid)
                    continue;
                others.Add(bridge);
            }

            // FindObjectsByType order is not stable, so without this the same teammate can occupy a
            // different row on each machine.
            others.Sort((a, b) =>
                a.Object.InputAuthority.PlayerId.CompareTo(b.Object.InputAuthority.PlayerId));

            while (_rows.Count < others.Count)
                _rows.Add(CreateRow(_rows.Count));
            for (int i = others.Count; i < _rows.Count; i++)
                _rows[i].Root.gameObject.SetActive(false);

            var localPos = _local.GetNetworkAnchorPosition();
            float pulse = 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 8f));

            // #region agent log
            bool logNow = Time.unscaledTime >= _nextDistanceLog;
            if (logNow)
                _nextDistanceLog = Time.unscaledTime + 1f;
            // #endregion

            for (int i = 0; i < others.Count; i++)
            {
                var row = _rows[i];
                row.Root.gameObject.SetActive(true);

                var other = others[i];
                float dist = Vector3.Distance(localPos, other.GetNetworkAnchorPosition());
                int meters = Mathf.RoundToInt(dist);
                var name = ResolveName(other);
                var color = PlayerColorPalette.Get(other.ColorIndex);

                row.Swatch.color = color;
                // Teammate health comes from replicated state; the local player's own values stay
                // owned by the Cowsins HUD.
                row.Label.text = $"{name}  {meters}m  {Mathf.RoundToInt(other.SyncedHealth)}hp";
                row.Label.color = BandColor(dist, pulse);

                // #region agent log
                if (logNow)
                    LogDistancePair(other, localPos, dist);
                // #endregion
            }

            // Master-synced grace timer: show how long the team has been split.
            UpdateBreachHint();
            UpdateCheckpointHint();
        }

        // #region agent log
        float _nextDistanceLog;

        void LogDistancePair(FpsNetworkBridge other, Vector3 localPos, float dist)
        {
            var localAnchor = _local.NetworkAnchor;
            var otherAnchor = other.NetworkAnchor;

            AgentDebugLog.Write("D1", "PlayerDistanceHUD.Refresh", "distance_pair",
                "{\"localPlayerId\":" + _local.Object.InputAuthority.PlayerId +
                ",\"localNetId\":\"" + _local.Object.Id + "\"" +
                ",\"localAnchor\":\"" + (localAnchor != null ? localAnchor.name : "null") + "\"" +
                ",\"localAnchorInstanceId\":" + (localAnchor != null ? localAnchor.GetInstanceID() : 0) +
                ",\"localPos\":\"" + localPos.ToString("F3") + "\"" +
                ",\"otherPlayerId\":" + other.Object.InputAuthority.PlayerId +
                ",\"otherNetId\":\"" + other.Object.Id + "\"" +
                ",\"otherAnchor\":\"" + (otherAnchor != null ? otherAnchor.name : "null") + "\"" +
                ",\"otherAnchorInstanceId\":" + (otherAnchor != null ? otherAnchor.GetInstanceID() : 0) +
                ",\"otherPos\":\"" + other.GetNetworkAnchorPosition().ToString("F3") + "\"" +
                ",\"distance\":" + dist.ToString("F3") + "}");
        }
        // #endregion

        TextMeshProUGUI _breachHint;
        TextMeshProUGUI _checkpointHint;
        int _seenSaveSeq;
        float _saveFlashUntil;

        void UpdateBreachHint()
        {
            var manager = TeamDistanceManager.Instance;
            float breach = manager != null ? manager.BreachSeconds : 0f;
            float grace = manager != null ? manager.SeparationGraceSeconds : 5f;

            if (_breachHint == null && _list != null)
            {
                var go = new GameObject("BreachHint", typeof(RectTransform));
                go.transform.SetParent(_list, false);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(0f, 8f);
                rt.sizeDelta = new Vector2(280f, 28f);
                _breachHint = go.AddComponent<TextMeshProUGUI>();
                _breachHint.fontSize = 18f;
                _breachHint.alignment = TextAlignmentOptions.MidlineRight;
                _breachHint.raycastTarget = false;
            }

            if (_breachHint == null)
                return;

            if (breach <= 0.05f)
            {
                _breachHint.gameObject.SetActive(false);
                return;
            }

            float left = Mathf.Max(0f, grace - breach);
            _breachHint.gameObject.SetActive(true);
            _breachHint.text = left > 0.05f
                ? $"TEAM SPLIT  {left:0.0}s"
                : "TEAM FAILED";
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 10f));
            _breachHint.color = new Color(1f, 0.2f, 0.15f, pulse);
        }

        void UpdateCheckpointHint()
        {
            var manager = TeamDistanceManager.Instance;
            if (_checkpointHint == null && _list != null)
            {
                var go = new GameObject("CheckpointHint", typeof(RectTransform));
                go.transform.SetParent(_list, false);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(0f, 36f);
                rt.sizeDelta = new Vector2(280f, 28f);
                _checkpointHint = go.AddComponent<TextMeshProUGUI>();
                _checkpointHint.fontSize = 18f;
                _checkpointHint.alignment = TextAlignmentOptions.MidlineRight;
                _checkpointHint.raycastTarget = false;
            }

            if (_checkpointHint == null)
                return;

            if (manager != null && manager.SaveSeq != _seenSaveSeq)
            {
                _seenSaveSeq = manager.SaveSeq;
                if (_seenSaveSeq > 0)
                    _saveFlashUntil = Time.unscaledTime + 2.5f;
            }

            if (Time.unscaledTime < _saveFlashUntil)
            {
                _checkpointHint.gameObject.SetActive(true);
                _checkpointHint.text = manager != null
                    ? $"CHECKPOINT {manager.SavedCheckpointId} SAVED"
                    : "CHECKPOINT SAVED";
                _checkpointHint.color = new Color(0.35f, 1f, 0.55f, 1f);
                return;
            }

            if (manager != null && manager.PendingCheckpointId > 0)
            {
                _checkpointHint.gameObject.SetActive(true);
                _checkpointHint.text =
                    $"SAVE {manager.PendingCheckpointId}  {manager.CountPendingVisits()}/{manager.RequiredVisitCount()}";
                _checkpointHint.color = new Color(0.45f, 0.85f, 1f, 1f);
                return;
            }

            _checkpointHint.gameObject.SetActive(false);
        }

        static string ResolveName(FpsNetworkBridge bridge)
        {
            var stats = bridge.GetComponent<AvocadoShark.PlayerStats>();
            if (stats != null)
            {
                var n = stats.PlayerName.ToString();
                if (!string.IsNullOrEmpty(n))
                    return n;
            }

            return $"Player {bridge.Object.InputAuthority.PlayerId}";
        }

        static Color BandColor(float dist, float pulse)
        {
            float critical = TeamDistanceManager.Instance != null
                ? TeamDistanceManager.Instance.MaxPlayerDistance
                : 30f;

            if (dist >= critical)
                return new Color(1f, 0.15f, 0.12f, pulse);
            if (dist >= TeamDistanceManager.DangerStart)
                return new Color(1f, 0.35f, 0.1f, pulse);
            if (dist >= TeamDistanceManager.WarningStart)
                return new Color(1f, 0.82f, 0.2f, 1f);
            return new Color(0.85f, 0.9f, 0.95f, 1f);
        }

        Row CreateRow(int index)
        {
            var rootGo = new GameObject($"Row{index}", typeof(RectTransform));
            var root = rootGo.GetComponent<RectTransform>();
            root.SetParent(_list, false);
            root.anchorMin = new Vector2(1f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(1f, 1f);
            root.anchoredPosition = new Vector2(0f, -index * 36f);
            root.sizeDelta = new Vector2(280f, 32f);

            var swatchGo = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
            var swatchRt = swatchGo.GetComponent<RectTransform>();
            swatchRt.SetParent(root, false);
            swatchRt.anchorMin = new Vector2(0f, 0.2f);
            swatchRt.anchorMax = new Vector2(0f, 0.8f);
            swatchRt.pivot = new Vector2(0f, 0.5f);
            swatchRt.anchoredPosition = Vector2.zero;
            swatchRt.sizeDelta = new Vector2(18f, 0f);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(root, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(26f, 0f);
            labelRt.offsetMax = Vector2.zero;
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 20f;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;

            return new Row
            {
                Root = root,
                Swatch = swatchGo.GetComponent<Image>(),
                Label = tmp
            };
        }
    }
}
#endif
