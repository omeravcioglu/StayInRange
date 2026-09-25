#if CMPSETUP_COMPLETE
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// FPS Engine only detects ladders on the Ladder layer. Game's playground
    /// has none, so a climbable volume is spawned when Game loads.
    /// </summary>
    public static class ParkourLevelBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureLadderInGame();
        }

        public static void EnsureLadderInGame()
        {
            if (SceneManager.GetActiveScene().name != "Game")
                return;

            var ladderLayer = LayerMask.NameToLayer("Ladder");
            if (ladderLayer < 0)
                return;

            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (col.gameObject.layer == ladderLayer)
                    return;
            }

            var ladder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ladder.name = "ParkourLadder";
            ladder.layer = ladderLayer;
            ladder.transform.position = new Vector3(2f, 3f, -34f);
            ladder.transform.localScale = new Vector3(1.2f, 6f, 0.25f);

            var renderer = ladder.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = new Color(0.75f, 0.45f, 0.15f);

            SceneManager.MoveGameObjectToScene(ladder, SceneManager.GetActiveScene());
        }
    }
}
#endif
