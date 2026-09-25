using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// WASD + mouse look for scene 2. Uses the Input System package (project is not on the old Input Manager).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SimpleScene2Player : MonoBehaviour
    {
        [SerializeField] float walkSpeed = 6f;
        [SerializeField] float lookSensitivity = 0.12f;
        [SerializeField] Transform lookPivot;

        CharacterController _controller;
        float _pitch;

        public void SetLookPivot(Transform pivot)
        {
            lookPivot = pivot;
        }

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (lookPivot == null)
                lookPivot = transform;
        }

        void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue() * lookSensitivity;
                transform.Rotate(0f, delta.x, 0f);
                _pitch = Mathf.Clamp(_pitch - delta.y, -80f, 80f);
                lookPivot.localEulerAngles = new Vector3(_pitch, 0f, 0f);
            }

            var input = ReadMove();
            var move = transform.TransformDirection(input) * walkSpeed;
            if (!_controller.isGrounded)
                move.y = -9.8f;
            _controller.Move(move * Time.deltaTime);
        }

        static Vector3 ReadMove()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return Vector3.zero;

            float x = 0f;
            float z = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)
                x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed)
                x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)
                z -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)
                z += 1f;

            var move = new Vector3(x, 0f, z);
            return move.sqrMagnitude > 1f ? move.normalized : move;
        }
    }
}
