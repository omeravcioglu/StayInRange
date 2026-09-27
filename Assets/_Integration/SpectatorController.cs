#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// What a dead player sees until they are revived: a "YOU DIED" title, then a camera following a
    /// living teammate.
    ///
    /// It follows the teammate rather than showing their actual view, and that is not a shortcut:
    /// another player's camera only exists on their machine, so short of streaming video there is
    /// nothing to show. What this does instead is put its own camera behind the teammate, oriented by
    /// the yaw that is already replicated, which reads as "watching over their shoulder" - and it
    /// only ever READS their replicated position, so it cannot touch their camera or their controls.
    ///
    /// Left and right mouse switch targets. Dead players are never valid targets - including the
    /// spectator themselves - and if whoever is being watched dies, the view moves on by itself.
    /// With nobody left standing it shows a waiting state until the team wipe restarts the round.
    ///
    /// It carries its own camera and AudioListener because the player's own camera is switched off
    /// while they are dead, which takes their listener with it. Exactly one listener is live at any
    /// moment. The ragdoll is a separate object entirely: this camera never follows the corpse.
    /// </summary>
    public class SpectatorController : MonoBehaviour
    {
        const float LookHeight = 1.5f;

        Camera _camera;
        Canvas _canvas;
        Text _title;
        RectTransform _titleRect;
        Text _label;
        FpsNetworkBridge _self;
        FpsNetworkBridge _target;
        bool _active;
        float _beganAt;

        readonly List<FpsNetworkBridge> _living = new List<FpsNetworkBridge>();

        public bool IsActive => _active;
        public FpsNetworkBridge Target => _target;

        /// <summary>1-based place of the watched teammate among those standing ("WATCHING · 2 OF 3").</summary>
        public int TargetIndex => _target != null ? _living.IndexOf(_target) + 1 : 0;

        /// <summary>How many teammates are standing to watch.</summary>
        public int TargetCount => _living.Count;

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

            BuildUi();
            SetActive(false);
        }

        public void Begin(FpsNetworkBridge self)
        {
            _self = self;
            _active = true;
            _beganAt = Time.unscaledTime;

            // Starts where the player died, looking at where they fell, so the first frame is not a
            // jump across the level. The first target pick then cuts to the teammate.
            if (self != null)
            {
                var anchor = self.GetNetworkAnchorPosition();
                transform.position = anchor + Vector3.up * 2.5f - self.transform.forward * 2.5f;
                transform.rotation = Quaternion.LookRotation(anchor - transform.position);
            }

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

            if (_canvas != null)
                _canvas.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (!_active)
                return;

            // Read directly rather than through Cowsins' InputManager: the dead player's gameplay
            // input is switched off, which is exactly the state this runs in. The arrow keys (and
            // A / D) do the same as the mouse buttons, for anyone not holding a mouse.
            // A click on the pause menu is the menu's, not a switch of teammate.
            bool menuOpen = UI.UiInput.MenuOpen;
            var mouse = menuOpen ? null : Mouse.current;
            var keyboard = menuOpen ? null : Keyboard.current;
            bool next = (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                        (keyboard != null && (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame));
            bool previous = (mouse != null && mouse.rightButton.wasPressedThisFrame) ||
                            (keyboard != null && (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame));
            if (next)
                PickNextTarget(1);
            else if (previous)
                PickNextTarget(-1);

            if (!IsValidTarget(_target))
                PickNextTarget(1);

            // Kept for the HUD's "WATCHING · n OF m"; the list is reused, not rebuilt.
            _living.Clear();
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (IsValidTarget(bridge))
                    _living.Add(bridge);
            }
            _living.Sort((a, b) => a.Owner.PlayerId.CompareTo(b.Owner.PlayerId));

            Follow();
            UpdateUi();
        }

        void Follow()
        {
            if (_target == null)
                return;

            var death = PlayerTuning.Active.death;
            var anchor = _target.GetNetworkAnchorPosition();
            var yaw = Quaternion.Euler(0f, _target.GetGameplayYaw(), 0f);
            var wanted = anchor + yaw * death.spectatorOffset;
            var lookAt = anchor + Vector3.up * LookHeight;

            // A wall between the camera and the teammate would otherwise leave the spectator staring
            // at masonry, which in a labyrinth is most of the time.
            if (Physics.Linecast(lookAt, wanted, out var hit,
                    HiddenSpawnUtility.DefaultSightBlockers(), QueryTriggerInteraction.Ignore))
            {
                wanted = hit.point + hit.normal * 0.25f;
            }

            float dt = Time.deltaTime;
            transform.position = Vector3.Lerp(transform.position, wanted,
                1f - Mathf.Exp(-death.spectatorFollowSharpness * dt));
            var wantedRotation = Quaternion.LookRotation(lookAt - transform.position);
            transform.rotation = Quaternion.Slerp(transform.rotation, wantedRotation,
                1f - Mathf.Exp(-death.spectatorTurnSharpness * dt));
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
                var offset = PlayerTuning.Active.death.spectatorOffset;
                transform.position = anchor + Quaternion.Euler(0f, next.GetGameplayYaw(), 0f) * offset;
                transform.rotation = Quaternion.LookRotation(anchor + Vector3.up * LookHeight - transform.position);
            }
        }

        List<FpsNetworkBridge> LivingTargets()
        {
            var list = new List<FpsNetworkBridge>();
            foreach (var bridge in FpsNetworkBridge.All)
            {
                if (IsValidTarget(bridge))
                    list.Add(bridge);
            }

            // A stable order, so the cycle does not jump around. By owner: players are spawned
            // without an input authority, so that would be None for everybody.
            list.Sort((a, b) => a.Owner.PlayerId.CompareTo(b.Owner.PlayerId));
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

        #region UI

        void BuildUi()
        {
            var canvasGo = new GameObject("SpectatorUI");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 400;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var titleGo = new GameObject("YouDied", typeof(RectTransform));
            _titleRect = titleGo.GetComponent<RectTransform>();
            _titleRect.SetParent(canvasGo.transform, false);
            _titleRect.anchorMin = new Vector2(0.5f, 0.5f);
            _titleRect.anchorMax = new Vector2(0.5f, 0.5f);
            _titleRect.pivot = new Vector2(0.5f, 0.5f);
            _titleRect.sizeDelta = new Vector2(1400f, 200f);

            _title = titleGo.AddComponent<Text>();
            _title.text = "YOU DIED";
            _title.alignment = TextAnchor.MiddleCenter;
            _title.fontSize = 120;
            _title.color = new Color(0.85f, 0.09f, 0.09f, 1f);
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;
            _title.verticalOverflow = VerticalWrapMode.Overflow;
            _title.raycastTarget = false;
            // Display face: the loudest line in the game.
            _title.font = GameFontSet.LegacyDisplayOrDefault();
            var shadow = titleGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            shadow.effectDistance = new Vector2(4f, -4f);

            var labelGo = new GameObject("SpectatorLabel", typeof(RectTransform));
            var rect = labelGo.GetComponent<RectTransform>();
            rect.SetParent(canvasGo.transform, false);
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 90f);
            rect.sizeDelta = new Vector2(1200f, 120f);

            _label = labelGo.AddComponent<Text>();
            _label.alignment = TextAnchor.LowerCenter;
            _label.fontSize = 30;
            _label.color = new Color(0.92f, 0.92f, 0.92f, 0.9f);
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.raycastTarget = false;
            // Body face: this label sits at the bottom of the screen and has to stay readable.
            _label.font = GameFontSet.LegacyBodyOrDefault();
            var labelShadow = labelGo.AddComponent<Shadow>();
            labelShadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
            labelShadow.effectDistance = new Vector2(2f, -2f);
        }

        /// <summary>
        /// The title fades in large in the middle of the screen, holds, then shrinks up to the top
        /// edge and stays there - clear enough to say what happened, out of the way of the view.
        /// </summary>
        void UpdateUi()
        {
            // The redesigned HUD draws YOU DIED and the spectating screen itself (MomentDirector);
            // this label is only the fallback for when it is not there.
            bool redesigned = UI.MomentDirector.Instance != null;
            if (_canvas != null && _canvas.gameObject.activeSelf == redesigned)
                _canvas.gameObject.SetActive(!redesigned);
            if (redesigned)
                return;

            float centreSeconds = PlayerTuning.Active.death.youDiedCentreSeconds;
            float age = Time.unscaledTime - _beganAt;

            float fadeIn = Mathf.Clamp01(age / 0.45f);
            float dock = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((age - centreSeconds) / 0.6f));

            _titleRect.anchoredPosition = new Vector2(0f, Mathf.Lerp(40f, 470f, dock));
            _titleRect.localScale = Vector3.one * Mathf.Lerp(Mathf.Lerp(1.25f, 1f, fadeIn), 0.42f, dock);
            var colour = _title.color;
            colour.a = fadeIn;
            _title.color = colour;

            if (_label == null)
                return;

            if (_target == null)
            {
                _label.text = "No teammates left standing.\nWaiting for the round to restart...";
                return;
            }

            _label.text = "Watching " + _target.DisplayName +
                          "\nLeft / Right mouse to switch teammate";
        }

        #endregion
    }
}
#endif
