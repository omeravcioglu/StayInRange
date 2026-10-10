using System.Collections;
using System.Collections.Generic;
using cowsins;
using MalbersAnimations.Controller;
using MalbersAnimations.InputSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// The game's player as a prefab you drop into any scene and press Play: Cowsins first person
    /// and Malbers third person, with the game's own HUD, pause and settings. For trying out a level
    /// or a prop without the menu, a room or the Game scene. Two prefabs sit next to this script: one
    /// starts in first person, the other in third (the game switches modes with trigger zones in the
    /// level, which a test scene may not have).
    ///
    /// It is the character a match spawns: a DualPlayerController building the Cowsins rig, Steve in
    /// the game's body and the shoulder cameras from Resources/LocalDualPlayer - the recipe the
    /// networked player is built from, read at Play so the two never drift apart - with Steve dressed
    /// in the player colour by the game's own rule. What a match adds on top needs the network: lift,
    /// throw, revive and the collar.
    ///
    /// It starts exactly where the prefab stands, facing its blue arrow. A test player the level was
    /// saved with (a Cowsins controller, a Steve, Malbers' cameras) is switched off for the run rather
    /// than taken over, and so is the scene's own camera; the scene file keeps both.
    ///
    /// It stands down by itself where a player already exists: in Game.unity, which sets up its own
    /// offline player, during a network session, and when another debug character got there first.
    /// So it is safe to leave in a scene.
    /// </summary>
    [DefaultExecutionOrder(-10000)] // Before DualPlayerController builds the character, and before the
                                    // level's own test player wakes up.
    [DisallowMultipleComponent]
    public class DebugCharacter : MonoBehaviour
    {
        /// <summary>The recipe the networked player uses (FpsNetworkBridge.EnsureDualPlayer).</summary>
        const string GameRecipe = "LocalDualPlayer";

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        [Tooltip("Start in third person (Malbers) instead of first person (Cowsins).")]
        [SerializeField] bool startInThirdPerson;

        [Tooltip("Switch off the scene's other cameras tagged MainCamera, so the one you see is the player's.")]
        [SerializeField] bool disableSceneCameras = true;

        [Tooltip("Body colour: -1 for the colour you last picked in the lobby, or 0 orange, 1 blue, 2 pink, 3 lime.")]
        [SerializeField, Range(-1, 3)] int colour = -1;

        DualPlayerController _dual;
        bool _dressedForThirdPerson;

        int Colour => colour >= 0 ? colour : Mathf.Max(0, PlayerColorPalette.SavedChoice);

        void Awake()
        {
            string reason = StandDownReason();
            if (reason != null)
            {
                Debug.Log("[DebugCharacter] " + reason + " '" + name + "' stays off.", this);
                gameObject.SetActive(false);
                return;
            }

            // Upright whatever tilt the prefab was given: the player stands on the spot, facing its way.
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

            _dual = GetComponent<DualPlayerController>();
            if (_dual != null)
            {
                var recipe = Resources.Load<DualPlayerController>(GameRecipe);
                if (recipe != null)
                    _dual.CopySourcePrefabsFrom(recipe);
                else
                    Debug.LogWarning("[DebugCharacter] Resources/" + GameRecipe + " is missing; building from the prefab's own copy of it.", this);

                // Its own rig, here - never the level's test player, wherever that stands.
                _dual.IgnoreScenePlayers = true;
            }

            SwitchOffScenePlayers();
        }

        IEnumerator Start()
        {
            if (disableSceneCameras)
                DisableSceneCameras();

#if CMPSETUP_COMPLETE
            // After DualPlayerController has built the character, which the HUD reads.
            if (GetComponent<UI.HudRoot>() == null)
                gameObject.AddComponent<UI.HudRoot>();
#endif

            DressSteve();
            if (_dual == null)
                yield break;

            // Once Cowsins has started (its teleport needs it): feet on the prefab's spot, facing its
            // way, wherever the rig's prefab put its body.
            yield return null;
            _dual.TeleportGameplay(transform.position, transform.eulerAngles.y);

            if (!startInThirdPerson)
                yield break;

            // The same switch the level's trigger zones use.
            yield return null;
            _dual.EnterThirdPerson();
        }

        void Update()
        {
            // As the game does: Steve is dressed again the first time it wakes for third person.
            if (_dressedForThirdPerson || _dual == null || !_dual.IsThirdPerson)
                return;

            _dressedForThirdPerson = true;
            DressSteve();
        }

        string StandDownReason()
        {
            if (SceneManager.GetActiveScene().name == "Game")
                return "Game.unity sets up its own player when played directly.";

            foreach (var runner in Fusion.NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return "A network session is running and spawns its own players.";
            }

            foreach (var other in FindObjectsByType<DualPlayerController>(FindObjectsSortMode.None))
            {
                if (other != null && other.gameObject != gameObject && other.isActiveAndEnabled)
                    return "There is already a player in the scene ('" + other.name + "').";
            }

            return null;
        }

        /// <summary>
        /// Steve in the player colour, exactly as the game dresses a player's own Steve
        /// (FpsNetworkBridge.ApplyIdentity): the colour's skin when the library has one per colour,
        /// otherwise its one skin with the skinned meshes painted - props and weapons keep their own.
        /// </summary>
        void DressSteve()
        {
            var steve = _dual != null ? _dual.SteveRoot : null;
            if (steve == null)
                return;

            var library = CharacterSkinLibrary.Load();
            bool perColour = library != null && library.Count >= PlayerColorPalette.Count;
            CharacterSelection.Apply(steve, perColour ? Colour : 0);
            if (perColour)
                return;

            var tint = PlayerColorPalette.Get(Colour);
            foreach (var renderer in steve.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (var material in renderer.materials)
                {
                    if (material == null)
                        continue;
                    if (material.HasProperty(BaseColorId))
                        material.SetColor(BaseColorId, tint);
                    if (material.HasProperty(ColorId))
                        material.SetColor(ColorId, tint);
                }
            }
        }

        /// <summary>
        /// The player rigs the level was saved with, switched off before they wake so this is the
        /// only player: a Cowsins controller (the whole rig - body, camera, managers - is the parent
        /// of its Player body), a Steve somebody plays, Malbers' camera rig. Animals and creatures
        /// without player input keep running.
        /// </summary>
        void SwitchOffScenePlayers()
        {
            var rigs = new HashSet<GameObject>();

            foreach (var movement in FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None))
            {
                var body = movement.transform;
                rigs.Add(body.parent != null ? body.parent.gameObject : body.gameObject);
            }

            foreach (var animal in FindObjectsByType<MAnimal>(FindObjectsSortMode.None))
            {
                if (animal.GetComponentInChildren<MInputLink>(true) != null)
                    rigs.Add(animal.gameObject);
            }

            foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t.name == "Cameras CM3")
                    rigs.Add(t.gameObject);
            }

            foreach (var rig in rigs)
            {
                if (rig == null || rig == gameObject || rig.transform.IsChildOf(transform))
                    continue;

                rig.SetActive(false);
                Debug.Log("[DebugCharacter] Switched off '" + rig.name + "', a player the scene already had, for this run.", rig);
            }
        }

        void DisableSceneCameras()
        {
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (cam == null || cam.transform.IsChildOf(transform))
                    continue;
                if (!cam.CompareTag("MainCamera") && !cam.name.Contains("Main Camera"))
                    continue;

                cam.enabled = false;
                var listener = cam.GetComponent<AudioListener>();
                if (listener != null)
                    listener.enabled = false;
                Debug.Log("[DebugCharacter] Switched off the scene camera '" + cam.name + "' for the player's own.", cam);
            }
        }

        /// <summary>A body-sized outline and a facing line, so the prefab can be placed in the editor.</summary>
        void OnDrawGizmos()
        {
            var position = transform.position;
            Gizmos.color = startInThirdPerson ? new Color(0.25f, 0.66f, 1f) : new Color(1f, 0.6f, 0.24f);
            Gizmos.DrawWireCube(position + Vector3.up * 0.85f, new Vector3(0.6f, 1.7f, 0.6f));
            Gizmos.DrawWireSphere(position + Vector3.up * 1.75f, 0.2f);
            Gizmos.DrawLine(position + Vector3.up * 1.6f, position + Vector3.up * 1.6f + transform.forward * 0.8f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(position + Vector3.up * 2.1f, startInThirdPerson ? "Debug Character (TPS)" : "Debug Character (FPS)");
#endif
        }
    }
}
