using System;
using System.Collections.Generic;
using cowsins;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// The game's own settings - video, sound, look sensitivity and key bindings - kept in
    /// PlayerPrefs under CollarCali.Settings.* and applied by the game's own code.
    ///
    /// Only what the player has changed is ever applied, so an untouched install runs exactly as the
    /// project is set up. Cowsins' settings manager is deliberately not used: creating it applies its
    /// own defaults on load - a quality level, a 60 fps cap, fullscreen at the highest resolution.
    ///
    /// Key bindings are the one thing stored Cowsins' way (one PlayerPrefs string per binding, keyed
    /// by map, action and index), so Cowsins' InputManager re-applies them to every player it builds.
    /// Third person and the telekinesis follow the same keys (LocalPlayerSettings, BodyTelekinesis).
    /// </summary>
    public static class GameSettings
    {
        const string Prefix = "CollarCali.Settings.";
        const string QualityKey = Prefix + "Quality";
        const string ResolutionWidthKey = Prefix + "ResolutionWidth";
        const string ResolutionHeightKey = Prefix + "ResolutionHeight";
        const string FullscreenKey = Prefix + "Fullscreen";
        const string VSyncKey = Prefix + "VSync";
        const string FrameRateKey = Prefix + "MaxFrameRate";
        const string VolumeKey = Prefix + "MasterVolume";
        const string MouseXKey = Prefix + "MouseSensitivityX";
        const string MouseYKey = Prefix + "MouseSensitivityY";
        const string PadKey = Prefix + "ControllerSensitivity";
        const string InvertYKey = Prefix + "InvertY";

        /// <summary>Cowsins' own defaults for the player, which the sliders start from.</summary>
        public const float DefaultMouseSensitivity = 4f;
        public const float DefaultControllerSensitivity = 35f;

        /// <summary>Frame caps offered; 0 is no cap.</summary>
        public static readonly int[] FrameRates = { 30, 60, 120, 144, 165, 240, 0 };

        /// <summary>Raised after any setting changes, so the live player can pick it up.</summary>
        public static event Action Changed;

        // What the project ran with before anything stored was applied, for RESET.
        static int _originalQuality = -1;
        static FullScreenMode _originalMode;
        static int _originalVSync;
        static int _originalFrameRate;
        static float _originalVolume = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            _originalQuality = QualitySettings.GetQualityLevel();
            _originalMode = Screen.fullScreenMode;
            _originalVSync = QualitySettings.vSyncCount;
            _originalFrameRate = Application.targetFrameRate;
            _originalVolume = AudioListener.volume;

            ApplyStored();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// The menu's connection manager sets its own frame cap when it wakes; a stored cap is put
        /// back after it, on every scene.
        /// </summary>
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (PlayerPrefs.HasKey(FrameRateKey))
                ApplyFrameRate(PlayerPrefs.GetInt(FrameRateKey));
            if (PlayerPrefs.HasKey(VolumeKey))
                AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey);
        }

        static void ApplyStored()
        {
            if (PlayerPrefs.HasKey(QualityKey))
                QualitySettings.SetQualityLevel(Mathf.Clamp(PlayerPrefs.GetInt(QualityKey), 0, QualitySettings.names.Length - 1), true);
            if (PlayerPrefs.HasKey(VSyncKey))
                QualitySettings.vSyncCount = PlayerPrefs.GetInt(VSyncKey) != 0 ? 1 : 0;
            if (PlayerPrefs.HasKey(FrameRateKey))
                ApplyFrameRate(PlayerPrefs.GetInt(FrameRateKey));
            if (PlayerPrefs.HasKey(VolumeKey))
                AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey);

            bool hasMode = PlayerPrefs.HasKey(FullscreenKey);
            bool hasSize = PlayerPrefs.HasKey(ResolutionWidthKey) && PlayerPrefs.HasKey(ResolutionHeightKey);
            if (!Application.isEditor && (hasMode || hasSize))
            {
                var mode = hasMode ? ModeFor(PlayerPrefs.GetInt(FullscreenKey) != 0) : Screen.fullScreenMode;
                int width = hasSize ? PlayerPrefs.GetInt(ResolutionWidthKey) : Screen.width;
                int height = hasSize ? PlayerPrefs.GetInt(ResolutionHeightKey) : Screen.height;
                Screen.SetResolution(width, height, mode);
            }
        }

        static void Save()
        {
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        #region Video

        public static string[] QualityNames => QualitySettings.names;

        public static int Quality => QualitySettings.GetQualityLevel();

        public static void SetQuality(int level)
        {
            level = Mathf.Clamp(level, 0, QualitySettings.names.Length - 1);
            QualitySettings.SetQualityLevel(level, true);
            // A quality level carries its own V-Sync; a choice made on this screen outranks it.
            if (PlayerPrefs.HasKey(VSyncKey))
                QualitySettings.vSyncCount = PlayerPrefs.GetInt(VSyncKey) != 0 ? 1 : 0;
            PlayerPrefs.SetInt(QualityKey, level);
            Save();
        }

        /// <summary>The screen sizes on offer, largest last, with the current one always among them.</summary>
        public static List<Vector2Int> Resolutions()
        {
            var sizes = new List<Vector2Int>();
            foreach (var resolution in Screen.resolutions)
            {
                var size = new Vector2Int(resolution.width, resolution.height);
                if (!sizes.Contains(size))
                    sizes.Add(size);
            }

            var current = CurrentResolution;
            if (!sizes.Contains(current))
                sizes.Add(current);
            sizes.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return sizes;
        }

        public static Vector2Int CurrentResolution => new Vector2Int(Screen.width, Screen.height);

        public static void SetResolution(Vector2Int size)
        {
            Screen.SetResolution(size.x, size.y, Screen.fullScreenMode);
            PlayerPrefs.SetInt(ResolutionWidthKey, size.x);
            PlayerPrefs.SetInt(ResolutionHeightKey, size.y);
            Save();
        }

        public static bool Fullscreen => Screen.fullScreenMode != FullScreenMode.Windowed;

        public static void SetFullscreen(bool fullscreen)
        {
            var mode = ModeFor(fullscreen);
            Screen.fullScreenMode = mode;
            if (fullscreen)
            {
                // Borderless at the screen's own size, which every monitor handles.
                var native = Screen.currentResolution;
                Screen.SetResolution(native.width, native.height, mode);
            }

            PlayerPrefs.SetInt(FullscreenKey, fullscreen ? 1 : 0);
            Save();
        }

        static FullScreenMode ModeFor(bool fullscreen) =>
            fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;

        public static bool VSync => QualitySettings.vSyncCount > 0;

        public static void SetVSync(bool on)
        {
            QualitySettings.vSyncCount = on ? 1 : 0;
            PlayerPrefs.SetInt(VSyncKey, on ? 1 : 0);
            Save();
        }

        /// <summary>The frame cap in force: 0 for none.</summary>
        public static int MaxFrameRate => Application.targetFrameRate > 0 ? Application.targetFrameRate : 0;

        public static void SetMaxFrameRate(int fps)
        {
            ApplyFrameRate(fps);
            PlayerPrefs.SetInt(FrameRateKey, fps);
            Save();
        }

        static void ApplyFrameRate(int fps) => Application.targetFrameRate = fps > 0 ? fps : -1;

        public static void ResetVideo()
        {
            foreach (var key in new[] { QualityKey, ResolutionWidthKey, ResolutionHeightKey, FullscreenKey, VSyncKey, FrameRateKey })
                PlayerPrefs.DeleteKey(key);

            if (_originalQuality >= 0)
                QualitySettings.SetQualityLevel(_originalQuality, true);
            QualitySettings.vSyncCount = _originalVSync;
            Application.targetFrameRate = _originalFrameRate;
            if (!Application.isEditor && Screen.fullScreenMode != _originalMode)
                Screen.fullScreenMode = _originalMode;
            Save();
        }

        #endregion

        #region Sound

        public static float MasterVolume => AudioListener.volume;

        public static void SetMasterVolume(float volume01)
        {
            volume01 = Mathf.Clamp01(volume01);
            AudioListener.volume = volume01;
            PlayerPrefs.SetFloat(VolumeKey, volume01);
            Save();
        }

        public static void ResetSound()
        {
            PlayerPrefs.DeleteKey(VolumeKey);
            AudioListener.volume = _originalVolume;
            Save();
        }

        #endregion

        #region Controls

        /// <summary>Whether the player has set any look setting; untouched, the player keeps its own tuning.</summary>
        public static bool HasLookSettings =>
            PlayerPrefs.HasKey(MouseXKey) || PlayerPrefs.HasKey(MouseYKey) ||
            PlayerPrefs.HasKey(PadKey) || PlayerPrefs.HasKey(InvertYKey);

        public static float MouseSensitivityX => PlayerPrefs.GetFloat(MouseXKey, DefaultMouseSensitivity);
        public static float MouseSensitivityY => PlayerPrefs.GetFloat(MouseYKey, DefaultMouseSensitivity);
        public static float ControllerSensitivity => PlayerPrefs.GetFloat(PadKey, DefaultControllerSensitivity);
        public static bool InvertY => PlayerPrefs.GetInt(InvertYKey, 0) != 0;

        public static void SetMouseSensitivityX(float value)
        {
            PlayerPrefs.SetFloat(MouseXKey, value);
            Save();
        }

        public static void SetMouseSensitivityY(float value)
        {
            PlayerPrefs.SetFloat(MouseYKey, value);
            Save();
        }

        public static void SetControllerSensitivity(float value)
        {
            PlayerPrefs.SetFloat(PadKey, value);
            Save();
        }

        public static void SetInvertY(bool invert)
        {
            PlayerPrefs.SetInt(InvertYKey, invert ? 1 : 0);
            Save();
        }

        public static void ResetControls()
        {
            foreach (var key in new[] { MouseXKey, MouseYKey, PadKey, InvertYKey })
                PlayerPrefs.DeleteKey(key);
            ResetBindings();
            Save();
        }

        #endregion

        #region Key bindings

        /// <summary>One line of the rebinding list: a Cowsins action, and its third-person twin if it has one.</summary>
        public readonly struct KeyAction
        {
            public readonly string Label;
            public readonly string Action;
            public readonly string ThirdPerson;
            public readonly bool Locked;

            public KeyAction(string label, string action, string thirdPerson = null, bool locked = false)
            {
                Label = label;
                Action = action;
                ThirdPerson = thirdPerson;
                Locked = locked;
            }
        }

        /// <summary>The keys on the rebinding screen, in its order. Locked ones are shown, greyed, for reference.</summary>
        public static readonly KeyAction[] KeyActions =
        {
            new KeyAction("Jump", "Jumping", "Jump"),
            new KeyAction("Crouch", "Crouching", "Crouch"),
            new KeyAction("Sprint", "Sprinting", "Sprint"),
            new KeyAction("Dash", "Dashing"),
            new KeyAction("Interact", "Interacting", "Interact"),
            new KeyAction("Fire", "Firing"),
            new KeyAction("Aim", "Aiming"),
            new KeyAction("Melee", "Melee"),
            new KeyAction("Inspect", "Inspect"),
            new KeyAction("Drop", "Drop"),
            new KeyAction("Grapple", "Grapple"),
            new KeyAction("Flashlight", "ToggleFlashLight"),
            new KeyAction("Scroll", "Scrolling", locked: true),
            new KeyAction("Move", "Movement", locked: true),
            new KeyAction("Reload", "Reloading", locked: true),
            new KeyAction("Pause", "Pause", locked: true),
            new KeyAction("Inventory", "InventoryOpen", locked: true),
        };

        /// <summary>The Cowsins action set, created early if the menu asks before any player exists.</summary>
        static PlayerActions Actions
        {
            get
            {
                if (InputManager.inputActions == null)
                {
                    InputManager.inputActions = new PlayerActions();
                    LoadCowsinsOverrides(InputManager.inputActions);
                }

                return InputManager.inputActions;
            }
        }

        public static InputAction FindAction(string name) => Actions.asset.FindAction(name);

        /// <summary>The binding a key line shows and rebinds: the action's first keyboard or mouse one.</summary>
        public static int KeyBindingIndex(InputAction action)
        {
            if (action == null)
                return -1;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var binding = action.bindings[i];
                if (binding.isComposite)
                    return i;
                if (binding.isPartOfComposite)
                    continue;
                var path = binding.effectivePath ?? string.Empty;
                if (path.StartsWith("<Keyboard>") || path.StartsWith("<Mouse>"))
                    return i;
            }

            return -1;
        }

        /// <summary>The effective path of an action's key binding, for the modes and helpers that follow it.</summary>
        public static string KeyPath(string actionName)
        {
            var action = FindAction(actionName);
            int index = KeyBindingIndex(action);
            return index >= 0 ? action.bindings[index].effectivePath : null;
        }

        /// <summary>How a key is written on the key caps: SPACE, L SHIFT, MOUSE 1, WASD.</summary>
        public static string KeyLabel(string actionName)
        {
            var action = FindAction(actionName);
            int index = KeyBindingIndex(action);
            if (index < 0)
                return "?";
            if (action.bindings[index].isComposite)
                return actionName == "Movement" ? "WASD" : action.GetBindingDisplayString(index).ToUpperInvariant();
            return LabelForPath(action.bindings[index].effectivePath);
        }

        public static string LabelForPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "?";

            switch (path)
            {
                case "<Mouse>/leftButton": return "MOUSE 1";
                case "<Mouse>/rightButton": return "MOUSE 2";
                case "<Mouse>/middleButton": return "MOUSE 3";
                case "<Mouse>/forwardButton": return "MOUSE 4";
                case "<Mouse>/backButton": return "MOUSE 5";
                case "<Mouse>/scroll": return "WHEEL";
                case "<Keyboard>/escape": return "ESC";
                case "<Keyboard>/leftShift": return "L SHIFT";
                case "<Keyboard>/rightShift": return "R SHIFT";
                case "<Keyboard>/leftCtrl": return "L CTRL";
                case "<Keyboard>/rightCtrl": return "R CTRL";
                case "<Keyboard>/leftAlt": return "L ALT";
                case "<Keyboard>/rightAlt": return "R ALT";
            }

            return InputControlPath.ToHumanReadableString(path,
                InputControlPath.HumanReadableStringOptions.OmitDevice).ToUpperInvariant();
        }

        /// <summary>
        /// Listens for the next key or mouse button and binds it to <paramref name="actionName"/>.
        /// Escape cancels. A key another line already uses is swapped over to this line's old key, so
        /// two actions never share one. Returns the operation, or null when the action cannot be rebound.
        /// </summary>
        public static InputActionRebindingExtensions.RebindingOperation StartRebind(string actionName,
            Action onDone, Action onCancelled)
        {
            var action = FindAction(actionName);
            int index = KeyBindingIndex(action);
            if (action == null || index < 0 || action.bindings[index].isComposite)
                return null;

            string oldPath = action.bindings[index].effectivePath;
            bool wasEnabled = action.enabled;
            action.Disable();

            var operation = action.PerformInteractiveRebinding(index)
                .WithControlsHavingToMatchPath("<Keyboard>")
                .WithControlsHavingToMatchPath("<Mouse>/leftButton")
                .WithControlsHavingToMatchPath("<Mouse>/rightButton")
                .WithControlsHavingToMatchPath("<Mouse>/middleButton")
                .WithControlsHavingToMatchPath("<Mouse>/forwardButton")
                .WithControlsHavingToMatchPath("<Mouse>/backButton")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.05f);

            operation.OnComplete(op =>
            {
                op.Dispose();
                if (wasEnabled)
                    action.Enable();

                string newPath = action.bindings[index].effectivePath;
                SwapAway(actionName, newPath, oldPath);
                SaveCowsinsOverrides(action);
                PlayerPrefs.Save();
                Changed?.Invoke();
                onDone?.Invoke();
            });
            operation.OnCancel(op =>
            {
                op.Dispose();
                if (wasEnabled)
                    action.Enable();
                onCancelled?.Invoke();
            });

            return operation.Start();
        }

        /// <summary>Gives <paramref name="oldPath"/> to whichever other line was using <paramref name="newPath"/>.</summary>
        static void SwapAway(string rebound, string newPath, string oldPath)
        {
            foreach (var line in KeyActions)
            {
                if (line.Locked || line.Action == rebound)
                    continue;
                var other = FindAction(line.Action);
                int index = KeyBindingIndex(other);
                if (index < 0 || other.bindings[index].isComposite || other.bindings[index].effectivePath != newPath)
                    continue;

                other.ApplyBindingOverride(index, oldPath);
                SaveCowsinsOverrides(other);
            }
        }

        public static void ResetBindings()
        {
            foreach (var line in KeyActions)
            {
                var action = FindAction(line.Action);
                if (action == null)
                    continue;
                action.RemoveAllBindingOverrides();
                SaveCowsinsOverrides(action);
            }

            PlayerPrefs.Save();
        }

        /// <summary>Cowsins' own format (InputManager.SaveBindingOverride), so its loading finds them.</summary>
        static void SaveCowsinsOverrides(InputAction action)
        {
            for (int i = 0; i < action.bindings.Count; i++)
                PlayerPrefs.SetString(action.actionMap + action.name + i, action.bindings[i].overridePath ?? string.Empty);
        }

        /// <summary>Cowsins' own loading (InputManager.LoadBindingOverride), for an action set made before any player.</summary>
        static void LoadCowsinsOverrides(PlayerActions actions)
        {
            foreach (var map in actions.asset.actionMaps)
            {
                foreach (var action in map.actions)
                {
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        string stored = PlayerPrefs.GetString(action.actionMap + action.name + i);
                        if (!string.IsNullOrEmpty(stored))
                            action.ApplyBindingOverride(i, stored);
                    }
                }
            }
        }

        #endregion
    }
}
