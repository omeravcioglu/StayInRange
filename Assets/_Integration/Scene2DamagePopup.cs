using TMPro;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// World-space damage numbers that do not depend on Emerald's Combat Text canvas.
    ///
    /// Uses TextMeshPro rather than the legacy TextMesh it started as. That is not cosmetic
    /// housekeeping: TextMesh can only draw a plain Font, so it could never use the game's SDF font
    /// asset, and it renders from a fixed-size bitmap atlas that goes soft the moment a number is
    /// close to the camera. TMP draws from a signed-distance-field atlas and stays crisp at any
    /// distance, which is most of the point of these being readable mid-fight.
    ///
    /// The display face is used deliberately: a damage number is a punchy one-or-two-character
    /// stinger, which is exactly what a display font is for, and legibility at small sizes - the
    /// reason the HUD uses the body face - does not apply to something this brief and this large.
    /// </summary>
    public class Scene2DamagePopup : MonoBehaviour
    {
        const float Lifetime = 0.85f;
        const float RiseSpeed = 1.4f;

        float _age;
        TMP_Text _text;

        public static void Show(Vector3 worldPosition, int amount) => Show(worldPosition, amount, headshot: false);

        /// <summary>
        /// A number rising from <paramref name="worldPosition"/> (pass the head, not the feet).
        /// Gold as the design has it; a headshot gets its own look - bigger, hotter, with a "!" -
        /// because headshots already do double damage and the player should see why.
        /// </summary>
        public static void Show(Vector3 worldPosition, int amount, bool headshot)
        {
            var go = new GameObject("DamageNumber");
            go.transform.position = worldPosition + Random.insideUnitSphere * 0.15f;
            // The 3D TextMeshPro, not the UGUI one: this lives in the world, not on a canvas.
            var text = go.AddComponent<TextMeshPro>();
            text.text = headshot ? amount + "!" : amount.ToString();
            text.fontSize = headshot ? 6.5f : 5f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = headshot ? new Color(1f, 0.54f, 0.24f, 1f) : new Color(1f, 0.83f, 0.28f, 1f);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            // Sized so a three-digit number still fits; TMP would otherwise wrap or clip it against
            // the default rect.
            text.rectTransform.sizeDelta = new Vector2(6f, 2f);

            // The redesign's display face with its ink rim, so the number reads against anything.
            var theme = UI.UiTheme.Active;
            var font = theme.GetFont(UI.FontRole.Display);
            if (font != null)
                text.font = font;
            var ink = theme.GetInkMaterial(UI.FontRole.Display, UI.Ink.Heavy);
            if (ink != null)
                text.fontSharedMaterial = ink;

            go.AddComponent<Scene2DamagePopup>()._text = text;
        }

        void Update()
        {
            _age += Time.deltaTime;
            transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);

            // Billboarded rather than parented to the camera, so it reads the same whichever player
            // is looking at it - including a dead one watching through the spectator camera.
            // Falls back to the audio listener's transform, which is what the spectator camera
            // carries - a dead player watching a teammate has no Camera.main.
            var camera = Camera.main;
            var reference = camera != null ? camera.transform : GameSfx.Listener;
            if (reference != null)
                transform.rotation = Quaternion.LookRotation(transform.position - reference.position);

            if (_text != null)
            {
                // TMP's own alpha channel, which does not disturb the colour the caller chose.
                _text.alpha = 1f - Mathf.Clamp01(_age / Lifetime);
            }

            if (_age >= Lifetime)
                Destroy(gameObject);
        }
    }
}
