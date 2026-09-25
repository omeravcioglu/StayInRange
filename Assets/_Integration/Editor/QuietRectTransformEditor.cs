#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CollarCali.Editor
{
    /// <summary>
    /// Replaces Unity's RectTransform inspector so its Scene-view anchor
    /// handles never call ScreenToWorldPoint (those "out of view frustum" errors).
    /// </summary>
    [CustomEditor(typeof(RectTransform), true)]
    [CanEditMultipleObjects]
    public class QuietRectTransformEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }

        void OnSceneGUI()
        {
        }
    }
}
#endif
