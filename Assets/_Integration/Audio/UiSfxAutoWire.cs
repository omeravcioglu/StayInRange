using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CollarCali
{
    /// <summary>
    /// Gives the menu and lobby buttons a click and a hover.
    ///
    /// Done by sweeping the live scene rather than by editing Menu.unity, for two reasons: the
    /// lobby's entire UI is built from code in LobbyController (there is nothing in the scene file
    /// to attach a sound to), and the menu belongs to Clean Multiplayer Pro, so hand-edited
    /// references there would be lost if that package were ever reimported.
    ///
    /// Cowsins' own buttons are deliberately left alone - CowsinsButton already plays its own hover
    /// and click, and it derives from Button, so it turns up in this sweep and has to be skipped
    /// explicitly or every in-game click would fire twice.
    /// </summary>
    public class UiSfxAutoWire : MonoBehaviour
    {
        /// <summary>Only these scenes. In-game UI is Cowsins' and already has sound.</summary>
        static readonly string[] Scenes = { "Menu", "Lobby" };

        float _nextSweep;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        static void Ensure()
        {
            if (FindFirstObjectByType<UiSfxAutoWire>() != null)
                return;

            var go = new GameObject("UiSfxAutoWire");
            DontDestroyOnLoad(go);
            go.AddComponent<UiSfxAutoWire>();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextSweep)
                return;
            // Repeated rather than once per scene load: the lobby rebuilds its roster rows as
            // players join, and each rebuild brings new buttons with it.
            _nextSweep = Time.unscaledTime + 0.5f;

            if (!InWiredScene())
                return;

            foreach (var button in FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                if (button == null || button is cowsins.CowsinsButton)
                    continue;
                if (button.GetComponent<UiSfxButton>() != null)
                    continue;

                button.gameObject.AddComponent<UiSfxButton>().Bind(button);
            }
        }

        static bool InWiredScene()
        {
            var active = SceneManager.GetActiveScene().name;
            for (int i = 0; i < Scenes.Length; i++)
            {
                if (Scenes[i] == active)
                    return true;
            }
            return false;
        }
    }

    /// <summary>One button's worth of feedback. Added by the sweep above, never authored.</summary>
    [DisallowMultipleComponent]
    public class UiSfxButton : MonoBehaviour, IPointerEnterHandler
    {
        SfxId _clickId = SfxId.UiClick;

        /// <summary>
        /// Gives a button its hover but no click, for one whose click already plays something more
        /// specific from its own handler. Claiming the component is also what keeps the sweep from
        /// adding the generic click on top.
        /// </summary>
        public static void HoverOnly(Button button)
        {
            if (button == null || button.GetComponent<UiSfxButton>() != null)
                return;
            button.gameObject.AddComponent<UiSfxButton>();
        }

        public void Bind(Button button)
        {
            if (button == null)
                return;

            _clickId = ClassifyByLabel(button) ? SfxId.UiBack : SfxId.UiClick;
            button.onClick.AddListener(PlayClick);
        }

        void PlayClick()
        {
            GameSfx.Play2D(_clickId);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            GameSfx.Play2D(SfxId.UiHover);
        }

        /// <summary>
        /// A leave / back / quit button gets the softer descending click, so backing out of a menu
        /// does not sound like confirming something.
        /// </summary>
        static bool ClassifyByLabel(Button button)
        {
            if (LooksLikeBack(button.name))
                return true;

            var text = button.GetComponentInChildren<Text>();
            if (text != null && LooksLikeBack(text.text))
                return true;

            var tmp = button.GetComponentInChildren<TMPro.TMP_Text>();
            return tmp != null && LooksLikeBack(tmp.text);
        }

        static bool LooksLikeBack(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            value = value.ToLowerInvariant();
            return value.Contains("leave") || value.Contains("back") || value.Contains("cancel")
                   || value.Contains("exit") || value.Contains("quit") || value.Contains("close");
        }
    }
}
