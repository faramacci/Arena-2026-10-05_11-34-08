using UnityEngine;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// Fixed overhead rig matching the Dota2 / LoL read: a locked pitch looking
    /// down at the arena with no player-relative yaw. Screen-space "up" maps to
    /// world +Z, which is what an arena fighter wants so a touch drag straight
    /// up the phone always walks the character away from the camera.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class MobaCameraRig : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float pitch = GameConfig.CameraPitch;
        [SerializeField] private float distance = GameConfig.CameraDistance;
        [SerializeField] private bool orthographic = GameConfig.CameraOrthographic;
        [SerializeField] private float orthographicSize = GameConfig.CameraOrthoSize;
        [SerializeField] private float positionLerp = 12f;

        private Camera cam;

        /// <summary>Rotated so +Y on the stick points "into" the screen.</summary>
        public Quaternion ControlRotation => Quaternion.Euler(0f, yaw, 0f);

        private float yaw => GameConfig.CameraYaw;

        private void Awake()
        {
            cam = GetComponent<Camera>();
            ApplyProjection();
            SnapNow();
        }

        private void OnValidate()
        {
            if (cam == null) cam = GetComponent<Camera>();
            ApplyProjection();
        }

        private void ApplyProjection()
        {
            if (cam == null) return;
            cam.orthographic = orthographic;
            cam.orthographicSize = orthographicSize;
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 220f;
        }

        public void SetTarget(Transform t)
        {
            target = t;
            SnapNow();
        }

        /// <summary>Place the rig immediately with no smoothing (used on match start).</summary>
        public void SnapNow()
        {
            Vector3 goal = ComputeGoal();
            transform.position = goal;
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        private void LateUpdate()
        {
            Vector3 goal = ComputeGoal();
            if (positionLerp <= 0f)
            {
                transform.position = goal;
            }
            else
            {
                float t = 1f - Mathf.Exp(-positionLerp * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, goal, t);
            }
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        private Vector3 ComputeGoal()
        {
            // Default focus is the arena centre; once a fighter exists, follow it.
            Vector3 focus = target != null
                ? new Vector3(target.position.x, 0f, target.position.z)
                : Vector3.zero;

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            return focus - rot * Vector3.forward * distance;
        }

        /// <summary>
        /// Converts a raw 2D stick vector (screen space, +Y up) into world space
        /// so gameplay code never has to reason about camera orientation.
        /// </summary>
        public Vector3 StickToWorld(Vector2 stick)
        {
            Vector3 flat = ControlRotation * new Vector3(stick.x, 0f, stick.y);
            flat.y = 0f;
            return Vector3.ClampMagnitude(flat, 1f);
        }

        /// <summary>World-space point under a screen pixel, projected onto y = 0.</summary>
        public Vector3 ScreenToWorldGround(Vector2 screenPos)
        {
            Ray ray = cam.ScreenPointToRay(screenPos);
            Plane ground = new Plane(Vector3.up, Vector3.zero);
            return ground.Raycast(ray, out float enter) ? ray.GetPoint(enter) : Vector3.zero;
        }
    }
}
