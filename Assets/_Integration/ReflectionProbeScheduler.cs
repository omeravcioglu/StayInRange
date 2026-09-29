using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Renders a scene's realtime reflection probes after it has loaded, one at a time, instead of
    /// all of them on its first frame.
    ///
    /// Game.unity has 58 realtime probes set to render once when they wake. Each renders the whole
    /// level six times - one per cube face - and they all did it on the frame the scene appeared,
    /// on top of the load itself. That frame is part of why a player arriving in a session took so
    /// long to show up for everybody else.
    ///
    /// Done at runtime, before the loaded scene draws its first frame, so the scene file is never
    /// touched: probes that would render on wake are switched to render on request, capped in
    /// resolution, and time-sliced so each one spreads its faces over several frames. Then they are
    /// rendered in turn, nearest to the camera first, starting a moment after the load. Until its
    /// turn comes a probe reflects the sky, as any probe does before it has rendered. Probes set to
    /// refresh every frame, or already driven by script, are left exactly as they are.
    /// </summary>
    public class ReflectionProbeScheduler : MonoBehaviour
    {
        /// <summary>Largest probe resolution kept. One probe was 1024, the cost of 64 of the others.</summary>
        const int MaxResolution = 256;

        /// <summary>Lets the frames right after a load - spawning, the first HUD - go first.</summary>
        const float StartDelaySeconds = 1.5f;

        /// <summary>A probe that never reports finished is not allowed to hold up the rest.</summary>
        const float PerProbeTimeoutSeconds = 5f;

        /// <summary>A frame this long or longer counts as a hitch in the load report.</summary>
        const float HitchSeconds = 0.25f;

        const float ReportWindowSeconds = 20f;

        static ReflectionProbeScheduler _instance;

        readonly List<ReflectionProbe> _queue = new List<ReflectionProbe>();
        Coroutine _routine;

        // Load report: how the first seconds after a scene load actually went, for the debug log.
        string _reportScene;
        float _reportStartedAt = -1f;
        int _reportFrames;
        int _reportHitches;
        float _reportLongest;
        int _reportProbes;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            _instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Play started directly in a scene: sceneLoaded may have fired before Register hooked it.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterFirstSceneLoad()
        {
            Take(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Take(scene);

        /// <summary>
        /// Takes over every realtime probe in <paramref name="scene"/> that would render on wake.
        /// Runs after the scene's Awake calls and before its first frame is drawn, which is when a
        /// probe set to render on wake actually renders.
        /// </summary>
        static void Take(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            var taken = new List<ReflectionProbe>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    if (probe == null || probe.mode != ReflectionProbeMode.Realtime ||
                        probe.refreshMode != ReflectionProbeRefreshMode.OnAwake)
                        continue;

                    probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                    probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
                    if (probe.resolution > MaxResolution)
                        probe.resolution = MaxResolution;
                    taken.Add(probe);
                }
            }

            var scheduler = Ensure();

            // The first scene of a play session arrives through both entry points; the second pass
            // finds nothing left to take and must not restart the report the first one began.
            if (taken.Count == 0 && scheduler._reportStartedAt >= 0f && scheduler._reportScene == scene.name)
                return;

            scheduler.BeginReport(scene.name, taken.Count);
            if (taken.Count > 0)
                scheduler.Enqueue(taken);
        }

        static ReflectionProbeScheduler Ensure()
        {
            if (_instance != null)
                return _instance;

            var go = new GameObject("ReflectionProbeScheduler");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ReflectionProbeScheduler>();
            return _instance;
        }

        void Enqueue(List<ReflectionProbe> probes)
        {
            _queue.AddRange(probes);
            if (_routine == null)
                _routine = StartCoroutine(RenderQueue());
        }

        IEnumerator RenderQueue()
        {
            float wait = StartDelaySeconds;
            while (wait > 0f)
            {
                yield return null;
                wait -= Time.unscaledDeltaTime;
            }

            float started = Time.realtimeSinceStartup;
            int rendered = 0;

            while (_queue.Count > 0)
            {
                var probe = TakeNearest();
                if (probe == null || !probe.isActiveAndEnabled)
                    continue;

                int renderId = probe.RenderProbe();
                rendered++;

                // One probe at a time, so at most one cube face renders in any frame.
                float giveUpAt = Time.realtimeSinceStartup + PerProbeTimeoutSeconds;
                while (probe != null && !probe.IsFinishedRendering(renderId) &&
                       Time.realtimeSinceStartup < giveUpAt)
                {
                    yield return null;
                }
            }

            // #region agent log
            AgentDebugLog.Write("P2", "ReflectionProbeScheduler.RenderQueue", "probes_rendered",
                "{\"rendered\":" + rendered +
                ",\"seconds\":" + (Time.realtimeSinceStartup - started).ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture) + "}");
            // #endregion
            _routine = null;
        }

        /// <summary>The queued probe closest to whatever camera is drawing, so what the player sees first is right first.</summary>
        ReflectionProbe TakeNearest()
        {
            var camera = Camera.main;
            var from = camera != null ? camera.transform.position : Vector3.zero;

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _queue.Count; i++)
            {
                var probe = _queue[i];
                if (probe == null)
                {
                    _queue.RemoveAt(i);
                    i--;
                    continue;
                }

                float distance = (probe.bounds.ClosestPoint(from) - from).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0)
                return null;

            var chosen = _queue[best];
            _queue.RemoveAt(best);
            return chosen;
        }

        #region Load report

        void BeginReport(string scene, int probes)
        {
            _reportScene = scene;
            _reportStartedAt = Time.realtimeSinceStartup;
            _reportFrames = 0;
            _reportHitches = 0;
            _reportLongest = 0f;
            _reportProbes = probes;
        }

        void Update()
        {
            if (_reportStartedAt < 0f)
                return;

            float frame = Time.unscaledDeltaTime;
            _reportFrames++;
            if (frame >= HitchSeconds)
                _reportHitches++;
            if (frame > _reportLongest)
                _reportLongest = frame;

            if (Time.realtimeSinceStartup - _reportStartedAt < ReportWindowSeconds)
                return;

            // #region agent log
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            AgentDebugLog.Write("P1", "ReflectionProbeScheduler.Update", "load_frames",
                "{\"scene\":\"" + _reportScene +
                "\",\"probesDeferred\":" + _reportProbes +
                ",\"frames\":" + _reportFrames +
                ",\"hitchesOver250ms\":" + _reportHitches +
                ",\"longestFrameSeconds\":" + _reportLongest.ToString("0.00", culture) +
                ",\"windowSeconds\":" + ReportWindowSeconds.ToString("0", culture) + "}");
            // #endregion
            _reportStartedAt = -1f;
        }

        #endregion
    }
}
