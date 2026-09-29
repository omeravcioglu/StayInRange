#if CMPSETUP_COMPLETE
using System.Collections;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Fusion's own scene manager with one change: the host lets the scene change reach everyone
    /// before it starts loading the scene itself.
    ///
    /// Fusion begins the host's load in the very call that issues the change, and Game.unity freezes
    /// the host for many seconds while it loads. The change only went out once that freeze ended, so
    /// the other players did not even start loading until the host had finished - two full loads back
    /// to back, which is why a joining player arrived long after the host. Waiting a fraction of a
    /// second first lets the change leave in the next few network sends, so every machine loads at
    /// the same time.
    ///
    /// Given to the runner through StartGameArgs.SceneManager in FusionConnection; nothing else
    /// about loading differs from NetworkSceneManagerDefault.
    /// </summary>
    public class CollarSceneManager : NetworkSceneManagerDefault
    {
        /// <summary>Long enough for several network sends at any tick rate, short enough to go unnoticed.</summary>
        const float PublishLeadSeconds = 0.4f;

        protected override IEnumerator LoadSceneCoroutine(SceneRef sceneRef, NetworkLoadSceneParameters sceneParams)
        {
            // Only the one issuing the change needs to wait; everyone else is already late.
            if (Runner != null && Runner.IsRunning && Runner.IsSharedModeMasterClient)
            {
                float until = Time.realtimeSinceStartup + PublishLeadSeconds;
                while (Time.realtimeSinceStartup < until)
                    yield return null;
            }

            yield return LoadNow(sceneRef, sceneParams);
        }

        // Called outside the iterator: a base call made from inside one compiles to unverifiable code.
        IEnumerator LoadNow(SceneRef sceneRef, NetworkLoadSceneParameters sceneParams) =>
            base.LoadSceneCoroutine(sceneRef, sceneParams);
    }
}
#endif
