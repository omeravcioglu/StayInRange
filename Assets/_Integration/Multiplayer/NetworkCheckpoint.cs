#if CMPSETUP_COMPLETE
using TMPro;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Save-point volume. The shared team checkpoint only advances after every living
    /// player has entered this pad. Place the TeamSavePoint prefab; give each instance
    /// a unique increasing Id.
    /// </summary>
    [AddComponentMenu("CollarCali/Team Save Point")]
    [RequireComponent(typeof(Collider))]
    public class NetworkCheckpoint : MonoBehaviour
    {
        [SerializeField, Tooltip("Unique and increasing. The team save moves here only after every living player enters this pad.")]
        int id = 1;

        public int Id => id;

        TextMeshPro _label;
        Renderer _padRenderer;
        bool _savedLook;

        public void SetId(int value)
        {
            id = value;
        }

        void Reset()
        {
            var col = GetComponent<Collider>();
            col.isTrigger = true;
        }

        void Awake()
        {
            EnsureVisuals();
        }

        void OnTriggerEnter(Collider other) => TryRecord(other);

        void OnTriggerStay(Collider other) => TryRecord(other);

        void TryRecord(Collider other)
        {
            if (!TryResolvePlayerId(other, out int playerId))
                return;

            var manager = TeamDistanceManager.Instance;
            if (manager == null)
                return;
            if (manager.SavedCheckpointId >= id)
                return;
            if (manager.PendingCheckpointId == id && manager.HasVisited(playerId))
                return;

            // Acknowledges standing on the pad while the team is still waiting on someone else. The
            // chime for the checkpoint actually advancing comes from TeamDistanceManager.
            //
            // Proxies of the other player run this trigger on this machine too, so without the
            // ownership check you would hear a confirmation for a pad you never stepped on.
            if (IsLocalPlayer(playerId))
                GameSfx.Play2D(SfxId.CheckpointPartial);

            manager.NotifyPlayerReached(id, playerId, transform.position);
        }

        void LateUpdate()
        {
            var manager = TeamDistanceManager.Instance;
            if (manager == null)
                return;

            bool saved = manager.SavedCheckpointId >= id;
            if (saved != _savedLook)
            {
                _savedLook = saved;
                ApplyLook(saved);
            }

            if (_label == null)
                return;

            if (saved)
            {
                _label.text = $"SAVE {id}\nSAVED";
                return;
            }

            if (manager.PendingCheckpointId == id)
            {
                _label.text = $"SAVE {id}\n{manager.CountPendingVisits()}/{manager.RequiredVisitCount()}";
                return;
            }

            _label.text = $"SAVE {id}";
        }

        public void EnsureVisuals()
        {
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true;

            var existing = GetComponent<Renderer>();
            if (existing != null)
                existing.enabled = false;

            var pad = transform.Find("Pad");
            if (pad == null)
            {
                var padGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                padGo.name = "Pad";
                pad = padGo.transform;
                pad.SetParent(transform, false);
                pad.localPosition = new Vector3(0f, 0.04f, 0f);
                pad.localScale = new Vector3(1f, 0.08f, 1f);
                var padCol = padGo.GetComponent<Collider>();
                if (padCol != null)
                {
                    if (Application.isPlaying)
                        Destroy(padCol);
                    else
                        DestroyImmediate(padCol);
                }
            }

            _padRenderer = pad.GetComponent<Renderer>();
            ApplyLook(false);

            var labelTf = transform.Find("Label");
            if (labelTf == null)
            {
                var labelGo = new GameObject("Label");
                labelTf = labelGo.transform;
                labelTf.SetParent(transform, false);
                labelTf.localPosition = new Vector3(0f, 2.1f, 0f);
                labelTf.localScale = Vector3.one;
            }

            _label = labelTf.GetComponent<TextMeshPro>();
            if (_label == null)
                _label = labelTf.gameObject.AddComponent<TextMeshPro>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontSize = 4f;
            _label.color = Color.white;
            _label.text = $"SAVE {id}";
            _label.raycastTarget = false;
        }

        void ApplyLook(bool saved)
        {
            if (_padRenderer == null)
                return;

            var color = saved
                ? new Color(0.25f, 0.85f, 0.45f, 0.55f)
                : new Color(0.2f, 0.75f, 1f, 0.4f);
            _padRenderer.sharedMaterial = MakePadMaterial(color);
        }

        static Material MakePadMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            return mat;
        }

        static bool TryResolvePlayerId(Collider other, out int playerId)
        {
            playerId = -1;
            if (other == null)
                return false;

            var bridge = other.GetComponentInParent<FpsNetworkBridge>();
            if (TryReadPlayerId(bridge, out playerId))
                return true;

            foreach (var dual in FindObjectsByType<DualPlayerController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (dual == null || !dual.OwnsCollider(other))
                    continue;

                bridge = dual.GetComponent<FpsNetworkBridge>()
                         ?? dual.GetComponentInParent<FpsNetworkBridge>();
                if (TryReadPlayerId(bridge, out playerId))
                    return true;
            }

            return false;
        }

        static bool IsLocalPlayer(int playerId)
        {
            var runner = NetworkCombatHooks.FindRunner();
            return runner == null || !runner.IsRunning || runner.LocalPlayer.PlayerId == playerId;
        }

        static bool TryReadPlayerId(FpsNetworkBridge bridge, out int playerId)
        {
            playerId = -1;
            if (bridge == null || bridge.Object == null || !bridge.Object.IsValid || bridge.IsDead)
                return false;

            playerId = bridge.Object.InputAuthority.PlayerId;
            return playerId >= 0;
        }
    }
}
#endif
