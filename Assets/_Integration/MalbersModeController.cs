#if CMPSETUP_COMPLETE
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Deprecated: Steve now lives on LocalDualPlayer via DualPlayerController.
    /// Kept so old scene refs don't throw missing-script until the scene is rebuilt.
    /// </summary>
    /// Use DualPlayerController on LocalDualPlayer instead.
    public class MalbersModeController : MonoBehaviour
    {
        void Awake()
        {
            Debug.LogWarning(
                "[MalbersMode] Deprecated. Use LocalDualPlayer + DualPlayerController. Disabling.");
            enabled = false;
        }

        public bool IsInThirdPerson =>
            FindFirstObjectByType<DualPlayerController>() is { IsThirdPerson: true };

        public void EnterThirdPerson() =>
            FindFirstObjectByType<DualPlayerController>()?.EnterThirdPerson();

        public void ExitToFirstPerson() =>
            FindFirstObjectByType<DualPlayerController>()?.ExitToFirstPerson();
    }
}
#endif
