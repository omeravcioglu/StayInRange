#if CMPSETUP_COMPLETE
using System;
using System.Reflection;
using Fusion;
using Photon.Realtime;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Keeps the Photon connection alive while the game is frozen loading a scene.
    ///
    /// Loading Game.unity stalls the main thread for many seconds - 17 s was measured in the editor
    /// ("Loaded scene ... Total Operation Time") - and nothing talks to the Photon server while it
    /// does. The server drops a client that goes quiet for about ten seconds, so a slow load ended the
    /// session: Fusion shut down, the loading screen turned into SOMETHING BROKE, and the scene then
    /// finished loading with no session behind it.
    ///
    /// Photon ships the cure: ConnectionHandler runs a background timer that sends acknowledgements
    /// whenever the main thread has not, keeping the connection up for as long as KeepAliveInBackground
    /// allows. Fusion creates its own Photon client and does not attach one, so this finds that client
    /// on each running NetworkRunner and attaches a single handler to it, re-pointing it whenever Fusion
    /// swaps clients (menu room list, lobby, game).
    ///
    /// The client is reached through the runner's private _cloudServices field, found by name and then
    /// by type so a Fusion update that renames the field still works. If neither is found this logs
    /// once and stands aside - the game behaves exactly as before, just without the protection.
    /// </summary>
    public class CloudKeepAlive : MonoBehaviour
    {
        const float ScanSeconds = 0.5f;

        /// <summary>Longest stall ridden out, in milliseconds. After this the connection is let go as normal.</summary>
        const int KeepAliveMilliseconds = 60000;

        static FieldInfo _cloudServicesField;
        static FieldInfo _clientField;
        static bool _reflectionFailed;

        ConnectionHandler _handler;
        RealtimeClient _client;
        float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindFirstObjectByType<CloudKeepAlive>() != null)
                return;

            var go = new GameObject("CloudKeepAlive");
            DontDestroyOnLoad(go);
            go.AddComponent<CloudKeepAlive>();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanSeconds;

            var client = FindClient();
            if (client == _client)
                return;

            _client = client;
            Attach(client);
        }

        void Attach(RealtimeClient client)
        {
            if (client == null)
            {
                // Nothing to keep alive between sessions; the handler idles until the next one.
                if (_handler != null)
                    _handler.Client = null;
                return;
            }

            if (_handler == null)
            {
                // Added only once a client exists: ConnectionHandler complains in Start if it has none.
                // Its OnEnable starts the background timer.
                _handler = gameObject.AddComponent<ConnectionHandler>();
                _handler.Id = "CollarCali keep-alive";
            }

            _handler.Client = client;
            _handler.KeepAliveInBackground = KeepAliveMilliseconds;
        }

        static RealtimeClient FindClient()
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner == null)
                    continue;

                var client = ClientOf(runner);
                if (client != null)
                    return client;
            }

            return null;
        }

        static RealtimeClient ClientOf(NetworkRunner runner)
        {
            if (_reflectionFailed)
                return null;

            try
            {
                if (_cloudServicesField == null)
                {
                    _cloudServicesField = typeof(NetworkRunner).GetField("_cloudServices",
                                              BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                          ?? FindField(typeof(NetworkRunner), t => t.Name == "CloudServices");
                }

                var services = _cloudServicesField != null ? _cloudServicesField.GetValue(runner) : null;
                if (services == null)
                    return null;

                if (_clientField == null)
                    _clientField = FindField(services.GetType(), t => typeof(RealtimeClient).IsAssignableFrom(t));

                if (_clientField == null)
                {
                    Fail("Fusion's cloud services hold no Photon client field this can reach.");
                    return null;
                }

                return _clientField.GetValue(services) as RealtimeClient;
            }
            catch (Exception e)
            {
                Fail(e.Message);
                return null;
            }
        }

        static FieldInfo FindField(Type owner, Func<Type, bool> matches)
        {
            for (var type = owner; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (matches(field.FieldType))
                        return field;
                }
            }

            return null;
        }

        static void Fail(string reason)
        {
            _reflectionFailed = true;
            Debug.LogWarning("[CollarCali] CloudKeepAlive could not reach Fusion's Photon client (" + reason +
                             "). Long scene loads may drop the connection again.");
        }
    }
}
#endif
