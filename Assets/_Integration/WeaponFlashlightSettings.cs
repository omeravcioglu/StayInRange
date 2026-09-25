using UnityEngine;

namespace CollarCali
{
    [CreateAssetMenu(menuName = "CollarCali/Weapon Flashlight Settings", fileName = "WeaponFlashlightSettings")]
    public class WeaponFlashlightSettings : ScriptableObject
    {
        [Header("Beam")]
        [Tooltip("How far the beam reaches, in meters.")]
        public float range = 95f;

        [Tooltip("Overall brightness. Lower this if nearby walls blow out white.")]
        public float intensity = 10f;

        [Tooltip("Outer cone width.")]
        [Range(1f, 160f)] public float spotAngle = 58f;

        [Tooltip("Bright core of the cone. Wider = less of a hot center.")]
        [Range(0f, 160f)] public float innerSpotAngle = 38f;

        public Color color = new Color(0.98f, 0.96f, 0.9f, 1f);

        [Header("Fill")]
        [Tooltip("Soft nearby light so dark / glancing surfaces still read.")]
        public float fillIntensity = 1.6f;
        public float fillRange = 10f;

        [Header("Close walls")]
        [Tooltip("Within this distance, brightness is pulled down so a wall in your face is not blinding.")]
        public float closeFadeDistance = 3.8f;

        [Tooltip("Brightness scale when something is right against the camera (0-1).")]
        [Range(0.05f, 1f)] public float closeMinScale = 0.1f;

        [Header("Placement")]
        public Vector3 cameraLocalPosition = new Vector3(0f, 0f, 0.55f);

        public bool startOn = true;
    }
}
