#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Small markers over teammates and revive stations.
    ///
    /// This is the counterpart to PlayerDistanceHUD, which lists the same distances in the corner.
    /// The list tells you HOW FAR your partner is; these tell you WHICH WAY - which is the thing you
    /// actually need in a labyrinth, and the thing a number cannot give you.
    ///
    /// Drawn as one screen-space canvas with elements moved by WorldToScreenPoint, rather than a
    /// world-space canvas per marker: it is cheaper, the text never ends up mirrored or edge-on, and
    /// markers can be clamped to the screen border when their target is off to one side.
    ///
    /// Markers are deliberately NOT occluded. Everything else in this game's audio and AI is careful
    /// about line of sight, but a marker whose whole job is to lead you through walls to your
    /// teammate would be useless if walls hid it. Distance fading is what keeps them unobtrusive.
    /// </summary>
    public class WorldMarkerHud : MonoBehaviour
    {
        const float LabelHeight = 2.1f;
        const float EdgeMargin = 48f;
        const float FarFade = 45f;

        Canvas _canvas;
        Camera _camera;
        readonly List<Marker> _pool = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        static void Ensure()
        {
            if (FindFirstObjectByType<WorldMarkerHud>() != null)
                return;

            var go = new GameObject("WorldMarkerHud");
            DontDestroyOnLoad(go);
            go.AddComponent<WorldMarkerHud>();
        }

        void Awake()
        {
            BuildCanvas();
        }

        void LateUpdate()
        {
            _camera = ResolveCamera();
            if (_camera == null)
            {
                HideFrom(0);
                return;
            }

            var local = NetworkCombatHooks.FindLocalBridge();
            int used = 0;

            bool anyoneDown = false;
            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (bridge == null || bridge.Object == null || !bridge.Object.IsValid)
                    continue;
                if (bridge.IsDead)
                    anyoneDown = true;
                if (bridge == local)
                    continue;

                used = DrawTeammate(bridge, used);
            }

            // Stations only appear when somebody is actually down. Permanent markers on every
            // station would be clutter for most of a run, and the one moment they matter is when a
            // body needs carrying to one.
            if (anyoneDown)
            {
                foreach (var station in FindObjectsByType<ReviveStation>(FindObjectsSortMode.None))
                {
                    if (station == null)
                        continue;
                    used = DrawStation(station, used);
                }
            }

            HideFrom(used);
        }

        #region Drawing

        int DrawTeammate(FpsNetworkBridge bridge, int index)
        {
            var world = bridge.GetNetworkAnchorPosition() + Vector3.up * LabelHeight;
            float distance = Vector3.Distance(_camera.transform.position, world);

            Color colour;
            string label;

            if (bridge.IsDead)
            {
                // A body reads differently from a living player on purpose: it is a task, not a
                // teammate you can call to.
                colour = new Color(0.95f, 0.25f, 0.25f, 1f);
                label = bridge.IsCarried ? "CARRIED  " + Mathf.RoundToInt(distance) + "m"
                                         : "DOWN  " + Mathf.RoundToInt(distance) + "m";
            }
            else
            {
                colour = PlayerColorPalette.Get(bridge.ColorIndex);
                label = Mathf.RoundToInt(distance) + "m";

                // Tinted towards the warning colours as the collar starts to stretch, using the same
                // thresholds the separation rule itself uses, so the marker and the rule agree.
                if (distance >= TeamDistanceManager.DangerStart)
                    colour = Color.Lerp(colour, new Color(1f, 0.25f, 0.2f), 0.75f);
                else if (distance >= TeamDistanceManager.WarningStart)
                    colour = Color.Lerp(colour, new Color(1f, 0.75f, 0.2f), 0.6f);
            }

            return Place(index, world, colour, label, distance);
        }

        int DrawStation(ReviveStation station, int index)
        {
            var world = station.transform.position + Vector3.up * 1.6f;
            float distance = Vector3.Distance(_camera.transform.position, world);
            return Place(index, world, new Color(0.35f, 1f, 0.55f, 1f),
                "REVIVE  " + Mathf.RoundToInt(distance) + "m", distance);
        }

        /// <summary>
        /// Puts one marker on screen, clamped to the border when its target is off to the side or
        /// behind the camera.
        /// </summary>
        int Place(int index, Vector3 world, Color colour, string label, float distance)
        {
            var marker = Get(index);
            var screen = _camera.WorldToScreenPoint(world);

            // Behind the camera comes back with a negative z and mirrored coordinates, which would
            // otherwise put the marker on the wrong side entirely.
            bool behind = screen.z < 0f;
            if (behind)
            {
                screen.x = Screen.width - screen.x;
                screen.y = Screen.height - screen.y;
            }

            bool offScreen = behind
                             || screen.x < EdgeMargin || screen.x > Screen.width - EdgeMargin
                             || screen.y < EdgeMargin || screen.y > Screen.height - EdgeMargin;

            screen.x = Mathf.Clamp(screen.x, EdgeMargin, Screen.width - EdgeMargin);
            screen.y = Mathf.Clamp(screen.y, EdgeMargin, Screen.height - EdgeMargin);

            marker.Root.position = new Vector3(screen.x, screen.y, 0f);

            // Fades with distance so a marker never dominates the screen, with a floor so it cannot
            // disappear entirely - losing your partner is the one thing this exists to prevent.
            float alpha = Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(distance / FarFade));
            colour.a = alpha;

            marker.Dot.color = colour;
            marker.Label.color = new Color(colour.r, colour.g, colour.b, alpha * 0.95f);
            marker.Label.text = label;

            // Shrunk at the edge: an off-screen marker is a direction, not a thing you are looking at.
            float scale = offScreen ? 0.75f : 1f;
            marker.Root.localScale = Vector3.one * scale;

            marker.Root.gameObject.SetActive(true);
            return index + 1;
        }

        void HideFrom(int index)
        {
            for (int i = index; i < _pool.Count; i++)
                _pool[i].Root.gameObject.SetActive(false);
        }

        /// <summary>
        /// The camera actually rendering right now.
        ///
        /// Camera.main alone is not enough: while the local player is dead their own camera is
        /// switched off and the spectator camera - deliberately untagged, so it cannot confuse the
        /// Cowsins camera sweeps - is the one drawing the screen.
        /// </summary>
        Camera ResolveCamera()
        {
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (camera != null && camera.isActiveAndEnabled && camera.targetTexture == null)
                    return camera;
            }

            return null;
        }

        #endregion

        #region Pool

        class Marker
        {
            public RectTransform Root;
            public Image Dot;
            public Text Label;
        }

        Marker Get(int index)
        {
            while (_pool.Count <= index)
                _pool.Add(CreateMarker());
            return _pool[index];
        }

        Marker CreateMarker()
        {
            var rootGo = new GameObject("Marker", typeof(RectTransform));
            var root = rootGo.GetComponent<RectTransform>();
            root.SetParent(_canvas.transform, false);
            root.sizeDelta = new Vector2(140f, 40f);

            var dotGo = new GameObject("Dot", typeof(RectTransform));
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.SetParent(root, false);
            dotRect.sizeDelta = new Vector2(11f, 11f);
            dotRect.anchoredPosition = new Vector2(0f, 12f);
            var dot = dotGo.AddComponent<Image>();
            dot.raycastTarget = false;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(root, false);
            labelRect.sizeDelta = new Vector2(150f, 22f);
            labelRect.anchoredPosition = new Vector2(0f, -6f);
            var label = labelGo.AddComponent<Text>();
            label.alignment = TextAnchor.MiddleCenter;
            label.fontSize = 15;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            return new Marker { Root = root, Dot = dot, Label = label };
        }

        void BuildCanvas()
        {
            var canvasGo = new GameObject("MarkerCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Under the wipe card and the grabbed overlay, above the world.
            _canvas.sortingOrder = 300;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        }

        #endregion
    }
}
#endif
