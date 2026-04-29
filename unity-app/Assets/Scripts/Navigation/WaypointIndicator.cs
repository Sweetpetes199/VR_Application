using UnityEngine;

namespace QSS.DeviceIntake.Navigation
{
    /// <summary>
    /// Floats an arrow + distance label in world space pointing at the target box.
    /// Attach to a Canvas or world-space GameObject that has a child arrow mesh
    /// and a TextMeshPro component for the label.
    /// </summary>
    public class WaypointIndicator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform _arrowMesh;
        [SerializeField] private TMPro.TextMeshPro _distanceLabel;
        [SerializeField] private TMPro.TextMeshPro _boxIdLabel;

        [Header("Settings")]
        [SerializeField] private float _floatHeightOffset = 0.3f;    // above anchor position
        [SerializeField] private float _arrowBobAmplitude = 0.04f;
        [SerializeField] private float _arrowBobSpeed = 2f;

        private Vector3 _targetWorldPos;
        private bool _active;
        private float _bobTimer;

        public void PointTo(Vector3 worldPosition, string boxId)
        {
            _targetWorldPos = worldPosition + Vector3.up * _floatHeightOffset;
            if (_boxIdLabel) _boxIdLabel.text = boxId;
            _active = true;
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            _active = false;
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_active) return;

            var cam = Camera.main;
            if (cam == null) return;

            // Place indicator between user and target, clamped to comfortable viewing distance
            var dirToTarget = (_targetWorldPos - cam.transform.position);
            var dist = dirToTarget.magnitude;
            transform.position = cam.transform.position + dirToTarget.normalized * Mathf.Min(dist, 1.2f);

            // Always face the user
            transform.LookAt(cam.transform.position);
            transform.Rotate(0, 180f, 0);

            // Bob animation on the arrow
            _bobTimer += Time.deltaTime * _arrowBobSpeed;
            if (_arrowMesh)
                _arrowMesh.localPosition = new Vector3(0, Mathf.Sin(_bobTimer) * _arrowBobAmplitude, 0);

            // Distance readout
            if (_distanceLabel)
                _distanceLabel.text = dist < 1f ? "Right here!" : $"{dist:F1}m";
        }
    }
}
