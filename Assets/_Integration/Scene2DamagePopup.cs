using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// World-space damage numbers that do not depend on Emerald's Combat Text canvas.
    /// </summary>
    public class Scene2DamagePopup : MonoBehaviour
    {
        float _age;
        TextMesh _text;
        const float Lifetime = 0.85f;

        public static void Show(Vector3 worldPosition, int amount)
        {
            var go = new GameObject("DamageNumber");
            go.transform.position = worldPosition + Vector3.up * 2.1f;

            var text = go.AddComponent<TextMesh>();
            text.text = amount.ToString();
            text.fontSize = 64;
            text.characterSize = 0.06f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(1f, 0.85f, 0.2f, 1f);

            go.AddComponent<Scene2DamagePopup>()._text = text;
        }

        void Update()
        {
            _age += Time.deltaTime;
            transform.position += Vector3.up * (1.4f * Time.deltaTime);

            var cam = Camera.main;
            if (cam != null)
            {
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            }

            if (_text != null)
            {
                var color = _text.color;
                color.a = 1f - Mathf.Clamp01(_age / Lifetime);
                _text.color = color;
            }

            if (_age >= Lifetime)
                Destroy(gameObject);
        }
    }
}
