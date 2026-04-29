using System.Collections.Generic;
using UnityEngine;
using QSS.DeviceIntake.Anchors;
using QSS.DeviceIntake.Data;

namespace QSS.DeviceIntake.Navigation
{
    /// <summary>
    /// Phase 3: loads the sticker queue and guides the user box by box.
    /// Attach to a persistent GameObject in the ApplyPhase scene.
    /// </summary>
    public class NavigationController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SpatialAnchorController _anchorController;
        [SerializeField] private WaypointIndicator _waypointIndicator;

        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _progressLabel;   // "3 / 12 stickered"
        [SerializeField] private TMPro.TextMeshProUGUI _boxInfoLabel;    // current target details

        private ScanSession _session;
        private Queue<DeviceRecord> _queue = new();
        private DeviceRecord _current;

        public void BeginNavigation(ScanSession session)
        {
            _session = session;

            _anchorController.LoadAnchorsForSession(session, OnAnchorsLoaded);
        }

        private void OnAnchorsLoaded()
        {
            BuildQueue();
            AdvanceToNext();
        }

        private void BuildQueue()
        {
            _queue.Clear();
            foreach (var record in _session.PendingStickers)
                _queue.Enqueue(record);

            UpdateProgressLabel();
        }

        private void AdvanceToNext()
        {
            if (_queue.Count == 0)
            {
                _waypointIndicator.Hide();
                SetBoxInfo("All stickers applied!");
                return;
            }

            _current = _queue.Dequeue();

            var worldPos = _anchorController.GetAnchorWorldPosition(_current.AnchorUuid);
            if (worldPos.HasValue)
            {
                _waypointIndicator.PointTo(worldPos.Value, _current.BoxId);
                SetBoxInfo(_current);
            }
            else
            {
                // Anchor not found — skip and move on
                Debug.LogWarning($"[Nav] Anchor not found for {_current.BoxId}, skipping.");
                AdvanceToNext();
            }
        }

        // Call this from a UI button or hand gesture after the sticker is placed
        public void ConfirmStickerApplied()
        {
            if (_current == null) return;

            _current.StickerApplied = true;
            _anchorController.HideAnchorMarker(_current.AnchorUuid);

            UpdateProgressLabel();
            AdvanceToNext();
        }

        private void UpdateProgressLabel()
        {
            if (_progressLabel == null || _session == null) return;
            _progressLabel.text = $"{_session.TotalStickered} / {_session.TotalComplete} stickered";
        }

        private void SetBoxInfo(DeviceRecord r)
        {
            if (_boxInfoLabel == null) return;
            _boxInfoLabel.text = $"{r.BoxId}\nS/N: {r.SerialNumber}\nMAC: {r.MacAddress}\n{r.PassFail}";
        }

        private void SetBoxInfo(string msg)
        {
            if (_boxInfoLabel) _boxInfoLabel.text = msg;
        }
    }
}
