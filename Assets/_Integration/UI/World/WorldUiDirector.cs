#if CMPSETUP_COMPLETE
using System;
using System.Collections.Generic;
using cowsins;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// The vendor UI that lives in the world rather than on the HUD, replaced with ours:
    /// - Cowsins' pickup icons: their canvases off (the object stays - the pickup writes to its
    ///   image), our PickupLabel in their place.
    /// - The "SPRING PAD" signs on Malbers' jump pads: the same words in the same place, in the
    ///   game's lettering instead of Unity's default font.
    /// - Emerald's combat-text canvas, which the combat bootstrap still spawns though nothing in the
    ///   level uses it.
    ///
    /// Swept once a second - pickups are dropped and converted at runtime - and every label moved
    /// each frame. Created with the local player's HUD and gone with it.
    /// </summary>
    public class WorldUiDirector : MonoBehaviour
    {
        const float SweepSeconds = 1f;

        Func<PlayerDependencies> _deps;
        readonly Dictionary<Pickeable, PickupLabel> _labels = new Dictionary<Pickeable, PickupLabel>();
        readonly Dictionary<Pickeable, string> _labelNames = new Dictionary<Pickeable, string>();
        readonly List<Pickeable> _gone = new List<Pickeable>();
        readonly HashSet<Canvas> _restyled = new HashSet<Canvas>();
        float _nextSweep;
        Camera _camera;

        public static WorldUiDirector Create(Func<PlayerDependencies> deps)
        {
            var go = new GameObject("WorldUi");
            var director = go.AddComponent<WorldUiDirector>();
            director._deps = deps;
            return director;
        }

        void OnDestroy()
        {
            foreach (var label in _labels.Values)
            {
                if (label != null)
                    Destroy(label.gameObject);
            }

            _labels.Clear();
        }

        void LateUpdate()
        {
            float now = Time.unscaledTime;
            if (now >= _nextSweep)
            {
                _nextSweep = now + SweepSeconds;
                Sweep();
            }

            var cam = ResolveCamera();
            var deps = _deps != null ? _deps() : null;
            var aimed = deps != null && deps.InteractManager != null ? deps.InteractManager.HighlightedInteractable : null;

            _gone.Clear();
            foreach (var pair in _labels)
            {
                if (pair.Value == null || !pair.Value.Tick(cam != null ? cam.transform : null, aimed == pair.Key))
                    _gone.Add(pair.Key);
            }

            foreach (var key in _gone)
                Drop(key);
        }

        void Drop(Pickeable key)
        {
            if (_labels.TryGetValue(key, out var label) && label != null)
                Destroy(label.gameObject);
            _labels.Remove(key);
            _labelNames.Remove(key);
        }

        void Sweep()
        {
            foreach (var pickeable in FindObjectsByType<Pickeable>(FindObjectsSortMode.None))
            {
                if (pickeable == null)
                    continue;

                Silence(pickeable);

                // A swap leaves a different gun on the floor under the same pickup.
                string name = NameOf(pickeable);
                if (_labelNames.TryGetValue(pickeable, out var shown) && shown != name)
                    Drop(pickeable);
                if (!_labels.ContainsKey(pickeable))
                {
                    _labels[pickeable] = PickupLabel.Create(pickeable, name, GlyphOf(pickeable));
                    _labelNames[pickeable] = name;
                }
            }

            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas == null)
                    continue;
                if (canvas.name == "JumPad")
                    RestyleSign(canvas);
                else if (canvas.name.StartsWith("Combat Text Canvas", StringComparison.Ordinal) && canvas.enabled)
                    canvas.enabled = false;
            }
        }

        /// <summary>Cowsins' ring and icon go dark; the pickup keeps writing to them unseen.</summary>
        static void Silence(Pickeable pickeable)
        {
            foreach (var canvas in pickeable.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas.enabled)
                    canvas.enabled = false;
            }
        }

        static string NameOf(Pickeable pickeable)
        {
            switch (pickeable)
            {
                case WeaponPickeable weapon:
                    return weapon.weapon != null && !string.IsNullOrEmpty(weapon.weapon._name) ? weapon.weapon._name : "Weapon";
                case BulletsPickeable _:
                    return "Ammo";
                case AttachmentPickeable _:
                    return "Attachment";
                default:
                    return "Pick up";
            }
        }

        static WeaponGlyph GlyphOf(Pickeable pickeable)
        {
            return pickeable is WeaponPickeable weapon ? CowsinsHudSource.GlyphFor(weapon.weapon) : WeaponGlyph.None;
        }

        /// <summary>The jump pad sign's words, kept where they were, redrawn in the game's lettering.</summary>
        void RestyleSign(Canvas canvas)
        {
            if (!_restyled.Add(canvas))
                return;

            foreach (var legacy in canvas.GetComponentsInChildren<Text>(true))
            {
                var from = legacy.rectTransform;
                var label = UiKit.CreateText(from.parent, legacy.name + " (game font)", legacy.text.ToUpperInvariant(),
                    TextStyle.Prompt.WithSize(legacy.fontSize).WithAlign(TextAlignmentOptions.Center));
                var to = label.rectTransform;
                to.anchorMin = from.anchorMin;
                to.anchorMax = from.anchorMax;
                to.pivot = from.pivot;
                to.anchoredPosition3D = from.anchoredPosition3D;
                to.sizeDelta = from.sizeDelta;
                to.localRotation = from.localRotation;
                to.localScale = from.localScale;
                label.color = UiTheme.Active.cream;
                label.textWrappingMode = TextWrappingModes.Normal;
                legacy.enabled = false;
            }
        }

        Camera ResolveCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled && _camera.targetTexture == null)
                return _camera;

            _camera = Camera.main;
            if (_camera != null && _camera.isActiveAndEnabled)
                return _camera;

            _camera = null;
            foreach (var candidate in Camera.allCameras)
            {
                if (candidate != null && candidate.isActiveAndEnabled && candidate.targetTexture == null)
                {
                    _camera = candidate;
                    break;
                }
            }

            return _camera;
        }
    }
}
#endif
