#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using CollarCali.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Small markers over teammates, bodies and revive stations.
    ///
    /// This is the counterpart to the team panel (UI/Hud/HudRoot), which lists the same distances in the corner.
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
    ///
    /// Drawn in the redesign's style: a player is their colour dot and name, a body is a skull
    /// (DOWN) or the grip hand (CARRIED), a station is the green cross - each with a small pointer.
    /// </summary>
    public class WorldMarkerHud : MonoBehaviour
    {
        const float LabelHeight = 2.1f;
        const float EdgeMargin = 48f;
        const float FarFade = 45f;
        const float NameRefreshSeconds = 1f;

        static readonly Color StationTick = UiTheme.Rgb(0x2CC45A);

        Canvas _canvas;
        Camera _camera;
        readonly List<Marker> _pool = new();
        readonly Dictionary<FpsNetworkBridge, string> _names = new();
        float _namesAt;

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
            // Under every HUD layer; the markers are part of the scene, not the interface.
            _canvas = UiKit.CreateCanvas("MarkerCanvas", UiLayers.World, parent: transform);
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
            RefreshNames();
            int used = 0;

            // The registries, not a scene search: this runs every frame.
            bool anyoneDown = false;
            foreach (var bridge in FpsNetworkBridge.All)
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
                foreach (var station in ReviveStation.Active)
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
            var theme = UiTheme.Active;
            var world = bridge.GetNetworkAnchorPosition() + Vector3.up * LabelHeight;
            float distance = Vector3.Distance(_camera.transform.position, world);
            int metres = Mathf.RoundToInt(distance);
            var name = NameOf(bridge);
            var colour = PlayerColorPalette.Get(bridge.ColorIndex);

            var marker = Get(index);
            if (bridge.IsDead)
            {
                // A body reads differently from a living player on purpose: it is a task, not a
                // teammate you can call to.
                bool carried = bridge.IsCarried;
                marker.Show(carried ? MarkerIcon.Hand : MarkerIcon.Skull, colour, theme.white,
                    carried ? MarkerText.Carried : MarkerText.Down, name, metres);
                marker.Tick.color = carried ? theme.warning : theme.danger;
            }
            else
            {
                // The label takes the collar's colours as the distance grows, on the same
                // thresholds the separation rule uses, so the marker and the rule agree.
                marker.Show(MarkerIcon.Dot, colour, theme.GetTetherColor(bridge == null ? 0f : DistanceFromLocal(bridge)),
                    MarkerText.Name, name, metres);
                marker.Tick.color = colour;
            }

            return Place(marker, index, world, distance);
        }

        /// <summary>The collar measures player to player, not camera to player - which differs while spectating.</summary>
        static float DistanceFromLocal(FpsNetworkBridge bridge)
        {
            var local = NetworkCombatHooks.FindLocalBridge();
            return local == null
                ? 0f
                : Vector3.Distance(local.GetNetworkAnchorPosition(), bridge.GetNetworkAnchorPosition());
        }

        int DrawStation(ReviveStation station, int index)
        {
            var world = station.transform.position + Vector3.up * 1.6f;
            float distance = Vector3.Distance(_camera.transform.position, world);

            var marker = Get(index);
            marker.Show(MarkerIcon.Cross, Color.white, UiTheme.Active.white, MarkerText.Revive, null,
                Mathf.RoundToInt(distance));
            marker.Tick.color = StationTick;
            return Place(marker, index, world, distance);
        }

        /// <summary>
        /// Puts one marker on screen, clamped to the border when its target is off to the side or
        /// behind the camera.
        /// </summary>
        int Place(Marker marker, int index, Vector3 world, float distance)
        {
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
            marker.Group.alpha = Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(distance / FarFade));

            // Shrunk at the edge, with a chevron saying which way: off screen it is a direction,
            // not a thing you are looking at.
            marker.Root.localScale = Vector3.one * (offScreen ? 0.8f : 1f);
            marker.SetEdge(offScreen ? (screen.x <= EdgeMargin + 1f ? -1 : screen.x >= Screen.width - EdgeMargin - 1f ? 1 : 0) : 0);

            if (!marker.Root.gameObject.activeSelf)
                marker.Root.gameObject.SetActive(true);
            return index + 1;
        }

        void HideFrom(int index)
        {
            for (int i = index; i < _pool.Count; i++)
            {
                if (_pool[i].Root.gameObject.activeSelf)
                    _pool[i].Root.gameObject.SetActive(false);
            }
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

            foreach (var camera in Camera.allCameras)
            {
                if (camera != null && camera.isActiveAndEnabled && camera.targetTexture == null)
                    return camera;
            }

            return null;
        }

        void RefreshNames()
        {
            if (Time.unscaledTime < _namesAt)
                return;
            _namesAt = Time.unscaledTime + NameRefreshSeconds;
            _names.Clear();
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (bridge != null && bridge.Object != null && bridge.Object.IsValid)
                    _names[bridge] = bridge.DisplayName;
            }
        }

        string NameOf(FpsNetworkBridge bridge)
        {
            return _names.TryGetValue(bridge, out var name) ? name : bridge.DisplayName;
        }

        #endregion

        #region Pool

        enum MarkerIcon
        {
            Dot,
            Skull,
            Hand,
            Cross,
        }

        enum MarkerText
        {
            Name,
            Down,
            Carried,
            Revive,
        }

        class Marker
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public IconStack Dot;
            public Image Skull;
            public IconStack Hand;
            public Image Cross;
            public TextMeshProUGUI Label;
            public Image Tick;
            public IconStack EdgeLeft;
            public IconStack EdgeRight;

            MarkerText _text = (MarkerText)(-1);
            string _name;
            int _metres = -1;
            int _edge = int.MinValue;

            public void Show(MarkerIcon icon, Color playerColour, Color labelColour, MarkerText text, string name, int metres)
            {
                Set(Dot, icon == MarkerIcon.Dot);
                Set(Skull, icon == MarkerIcon.Skull);
                Set(Hand, icon == MarkerIcon.Hand);
                Set(Cross, icon == MarkerIcon.Cross);
                if (icon == MarkerIcon.Dot)
                    Dot.SetTint(playerColour);

                Label.color = labelColour;
                if (text == _text && metres == _metres && name == _name)
                    return;

                _text = text;
                _metres = metres;
                _name = name;
                switch (text)
                {
                    case MarkerText.Down:
                        Label.SetText(name + " · DOWN {0}m", metres);
                        break;
                    case MarkerText.Carried:
                        Label.SetText(name + " · CARRIED {0}m", metres);
                        break;
                    case MarkerText.Revive:
                        Label.SetText("REVIVE {0}m", metres);
                        break;
                    default:
                        Label.SetText(name + " {0}m", metres);
                        break;
                }
            }

            /// <summary>-1 pinned to the left edge, 1 to the right, 0 on screen or top/bottom.</summary>
            public void SetEdge(int side)
            {
                if (side == _edge)
                    return;
                _edge = side;
                Set(EdgeLeft, side < 0);
                Set(EdgeRight, side > 0);
            }

            static void Set(Component component, bool visible)
            {
                if (component.gameObject.activeSelf != visible)
                    component.gameObject.SetActive(visible);
            }
        }

        Marker Get(int index)
        {
            while (_pool.Count <= index)
                _pool.Add(CreateMarker());
            return _pool[index];
        }

        Marker CreateMarker()
        {
            var theme = UiTheme.Active;
            var root = UiKit.CreateRect("Marker", _canvas.transform);
            root.sizeDelta = new Vector2(300f, 50f);
            var marker = new Marker { Root = root, Group = root.gameObject.AddComponent<CanvasGroup>() };

            // [chevron] [icon] label, with the tick centred underneath - the board's marker.
            var row = UiKit.CreateRow(root, "Row", 8f, TextAnchor.MiddleCenter);
            row.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), Vector2.zero);
            row.pivot = new Vector2(0.5f, 0.5f);

            marker.EdgeLeft = IconStack.Create(row, "Left", new Vector2(18f, 28f), theme.cream,
                IconStack.PlainLayer(UiSprites.ChevronLeftInk), IconStack.TintLayer(UiSprites.ChevronLeftLine));

            var icon = UiKit.CreateRect("Icon", row).Sized(26f, 26f);
            marker.Dot = UiKit.CreateSwatch(icon, Color.white, 20f);
            ((RectTransform)marker.Dot.transform).Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(20f, 20f));
            marker.Skull = UiKit.CreateImage(icon, "Skull", UiSprites.Skull, Color.white);
            marker.Skull.rectTransform.Fill();
            marker.Skull.preserveAspect = true;
            marker.Hand = UiKit.CreateIcon(icon, "Hand", 26f, UiSprites.HandInk, UiSprites.HandLine, theme.warning);
            ((RectTransform)marker.Hand.transform).Fill();
            marker.Cross = UiKit.CreateImage(icon, "Cross", UiSprites.Cross, Color.white);
            marker.Cross.rectTransform.Fill();
            marker.Cross.preserveAspect = true;

            marker.Label = UiKit.CreateText(row, "Label", string.Empty, TextStyle.Number.WithSize(26f));

            marker.EdgeRight = IconStack.Create(row, "Right", new Vector2(18f, 28f), theme.cream,
                IconStack.PlainLayer(UiSprites.ChevronRightInk), IconStack.TintLayer(UiSprites.ChevronRightLine));

            marker.Tick = UiKit.CreateImage(root, "Tick", UiSprites.Tick, Color.white);
            marker.Tick.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -22f), new Vector2(16f, 10f));

            marker.SetEdge(0);
            return marker;
        }

        #endregion
    }
}
#endif
