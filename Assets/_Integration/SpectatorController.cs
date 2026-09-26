#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// What a dead player watches until somebody revives them.
    ///
    /// It follows a living teammate rather than showing their actual view, and that distinction is
    /// not a shortcut: another player's camera only exists on their machine, so short of streaming
    /// video there is nothing to show. What this does instead is put a camera behind the teammate,
    /// oriented by the yaw that is already replicated, which reads as "watching over their shoulder".
    ///
    /// Left and right mouse switch targets. Dead players are never valid targets - including the
    /// spectator themselves - and if whoever is being watched dies, the view moves on by itself
    /// rather than sitting on a corpse.
    ///
    /// It carries its own camera and AudioListener because the player's own camera object is
    /// deactivated while they are down, which takes their listener with it. Exactly one listener is
    /// live at any moment.
    /// </summary>
    public class SpectatorController : MonoBehaviour
    {
        /// <summary>Behind and above the shoulder. Tuned to keep the teammate's body in frame.</summary>
        static readonly Vector3 FollowOffset = new Vector3(0f, 2.1f, -3.4f);

        const float LookHeight = 1.5f;
        const float PositionLerp = 9f;
        const float RotationLerp = 11f;

        Camera _camera;
        Text _label;
        FpsNetworkBridge _self;
        FpsNetworkBridge _target;
        bool _active;

        public bool IsActive => _active;
        public FpsNetworkBridge Target => _target;

        public static SpectatorController Ensure()
        {
            var existing = FindFirstObjectByType<SpectatorController>();
            if (existing != null)
                return existing;

            var go = new GameObject("SpectatorCamera");
            return go.AddComponent<SpectatorController>();
        }

        void Awake()
        {
            _camera = gameObject.GetComponent<Camera>();
            if (_camera == null)
                _camera = gameObject.AddComponent<Camera>();
            _camera.cullingMask = ~0;

            if (gameObject.GetComponent<AudioListener>() == null)
                gameObject.AddComponent<AudioListener>();

            BuildLabel();
            SetActive(false);
        }

        public void Begin(FpsNetworkBridge self)
        {
            _self = self;
            _active = true;
            SetActive(true);
            _target = null;
            PickNextTarget(1);
        }

        public void End()
        {
            _active = false;
            _target = null;
            SetActive(false);
        }

        void SetActive(bool on)
        {
            if (_camera != null)
                _camera.enabled = on;

            var listener = GetComponent<AudioListener>();
            if (listener != null)
                listener.enabled = on;

            if (_label != null)
                _label.canvas.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (!_active)
                return;

            // Read directly rather than through Cowsins' InputManager: the dead player's gameplay
            // input is switched off, which is exactly the state this runs in.
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                    PickNextTarget(1);
                else if (mouse.rightButton.wasPressedThisFrame)
                    PickNextTarget(-1);
            }

            if (!IsValidTarget(_target))
                PickNextTarget(1);

            Follow();
            UpdateLabel();
        }

        void Follow()
        {
            if (_target == null)
                return;

            var anchor = _target.GetNetworkAnchorPosition();
            var yaw = Quaternion.Euler(0f, _target.GetGameplayYaw(), 0f);
            var wanted = anchor + yaw * FollowOffset;
            var lookAt = anchor + Vector3.up * LookHeight;

            // A wall between the camera and the teammate would otherwise leave the spectator staring
            // at masonry, which in a labyrinth is most of the time.
            if (Physics.Linecast(lookAt, wanted, out var hit,
                    HiddenSpawnUtility.DefaultSightBlockers(), QueryTriggerInteraction.Ignore))
            {
                wanted = hit.point + hit.normal * 0.25f;
            }

            transform.position = Vector3.Lerp(transform.position, wanted, PositionLerp * Time.deltaTime);
            var wantedRotation = Quaternion.LookRotation(lookAt - transform.position);
            transform.rotation = Quaternion.Slerp(transform.rotation, wantedRotation,
                RotationLerp * Time.deltaTime);
        }

        #region Targets

        void PickNextTarget(int direction)
        {
            var candidates = LivingTargets();
            if (candidates.Count == 0)
            {
                _target = null;
                return;
            }

            int index = _target != null ? candidates.IndexOf(_target) : -1;
            index = index < 0 ? 0 : (index + direction + candidates.Count) % candidates.Count;
            var next = candidates[index];

            // Snapped rather than eased when switching: lerping across the level between two
            // teammates reads as the camera being thrown, not as a cut.
            if (next != _target)
            {
                _target = next;
                var anchor = next.GetNetworkAnchorPosition();
                transform.position = anchor + Quaternion.Euler(0f, next.GetGameplayYaw(), 0f) * FollowOffset;
                transform.rotation = Quaternion.LookRotation(anchor + Vector3.up * LookHeight - transform.position);
            }
        }

        List<FpsNetworkBridge> LivingTargets()
        {
            var list = new List<FpsNetworkBridge>();
            foreach (var bridge in FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (!IsValidTarget(bridge))
                    continue;
                list.Add(bridge);
            }

            // FindObjectsByType has no stable order, so without this the cycle jumps around.
            list.Sort((a, b) =>
                a.Object.InputAuthority.PlayerId.CompareTo(b.Object.InputAuthority.PlayerId));
            return list;
        }

        bool IsValidTarget(FpsNetworkBridge bridge)
        {
            return bridge != null
                   && bridge != _self
                   && bridge.Object != null
                   && bridge.Object.IsValid
                   && !bridge.IsDead;
        }

        #endregion

        #region Label

        void BuildLabel()
        {
            var canvasGo = new GameObject("SpectatorUI");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var labelGo = new GameObject("SpectatorLabel", typeof(RectTransform));
            var rect = labelGo.GetComponent<RectTransform>();
            rect.SetParent(canvasGo.transform, false);
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 90f);
            rect.sizeDelta = new Vector2(900f, 120f);

            _label = labelGo.AddComponent<Text>();
            _label.alignment = TextAnchor.LowerCenter;
            _label.fontSize = 30;
            _label.color = new Color(0.92f, 0.92f, 0.92f, 0.9f);
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.raycastTarget = false;
            // Body face: this label sits at the bottom of the screen and has to stay readable.
            _label.font = GameFontSet.LegacyBodyOrDefault();
        }

        void UpdateLabel()
        {
            if (_label == null)
                return;

            if (_target == null)
            {
                _label.text = "No living teammates to watch.\nWaiting for a revive.";
                return;
            }

            _label.text = "Spectating player " + _target.Object.InputAuthority.PlayerId +
                          "\nLeft / Right mouse to switch - carry my body to a revive station";
        }

        #endregion
    }
}
#endif
