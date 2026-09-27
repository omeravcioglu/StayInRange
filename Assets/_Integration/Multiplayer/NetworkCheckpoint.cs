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

        static readonly System.Collections.Generic.List<NetworkCheckpoint> Registry =
            new System.Collections.Generic.List<NetworkCheckpoint>();

        /// <summary>Every save pad in the level, for the compass's flag.</summary>
        public static System.Collections.Generic.IReadOnlyList<NetworkCheckpoint> All => Registry;

        /// <summary>The pad the team is heading for: the lowest id past the last save, or null.</summary>
        public static NetworkCheckpoint Next(int savedId)
        {
            NetworkCheckpoint best = null;
            foreach (var pad in Registry)
            {
                if (pad != null && pad.id > savedId && (best == null || pad.id < best.id))
                    best = pad;
            }

            return best;
        }

        void OnEnable()
        {
            if (!Registry.Contains(this))
                Registry.Add(this);
        }

        void OnDisable() => Registry.Remove(this);

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

            FaceCamera();

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

            // The pad is scaled flat and wide (6 x 1 x 6), and the label used to inherit that and
            // read six times too wide. Undo the parent's scale so the text keeps its proportions.
            var parentScale = transform.lossyScale;
            labelTf.localScale = new Vector3(SafeInverse(parentScale.x), SafeInverse(parentScale.y), SafeInverse(parentScale.z));

            _label = labelTf.GetComponent<TextMeshPro>();
            if (_label == null)
                _label = labelTf.gameObject.AddComponent<TextMeshPro>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontSize = 6f;
            _label.color = Color.white;
            _label.text = $"SAVE {id}";
            _label.raycastTarget = false;

            // The redesign's face and ink, so the pad reads like the rest of the UI.
            var theme = UI.UiTheme.Active;
            var font = theme.GetFont(UI.FontRole.Body);
            if (font != null)
                _label.font = font;
            var ink = theme.GetInkMaterial(UI.FontRole.Body, UI.Ink.Heavy);
            if (ink != null)
                _label.fontSharedMaterial = ink;
        }

        static float SafeInverse(float value) => Mathf.Abs(value) > 0.0001f ? 1f / value : 1f;

        /// <summary>
        /// Turns the label to whichever camera is live - the player's, or the spectator's while dead -
        /// upright, so it reads from anywhere around the pad.
        /// </summary>
        void FaceCamera()
        {
            var cam = Camera.main;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                foreach (var candidate in Camera.allCameras)
                {
                    if (candidate != null && candidate.isActiveAndEnabled && candidate.targetTexture == null)
                    {
                        cam = candidate;
                        break;
                    }
                }
            }

            if (cam == null)
                return;

            var away = _label.transform.position - cam.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.0001f)
                _label.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        void ApplyLook(bool saved)
        {
            if (_padRenderer == null)
                return;

            // Cream while waiting - the old blue was one of the player colours - and the revive green
            // once saved. Two shared materials, rather than a new one on every change.
            if (saved)
                _padRenderer.sharedMaterial = _savedMaterial != null ? _savedMaterial : (_savedMaterial = MakePadMaterial(SavedColour));
            else
                _padRenderer.sharedMaterial = _waitingMaterial != null ? _waitingMaterial : (_waitingMaterial = MakePadMaterial(WaitingColour));
        }

        static readonly Color WaitingColour = new Color(0.78f, 0.77f, 0.72f, 1f);
        static readonly Color SavedColour = new Color(0.23f, 0.86f, 0.44f, 1f);
        static Material _waitingMaterial;
        static Material _savedMaterial;

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

            // The owner, not the input authority: shared mode spawns players without one, so
            // it read None for everybody and no visit ever counted.
            playerId = bridge.Owner.PlayerId;
            return playerId >= 0;
        }
    }
}
#endif
