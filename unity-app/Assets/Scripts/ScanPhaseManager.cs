using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using QSS.DeviceIntake.Anchors;
using QSS.DeviceIntake.Data;
using QSS.DeviceIntake.Scanning;
using QSS.DeviceIntake.Sync;

namespace QSS.DeviceIntake
{
    /// <summary>
    /// Phase 1 scene orchestrator.
    /// Drop this on a persistent GameObject in ScanPhase.unity and wire
    /// up the Inspector references — no other glue code needed.
    ///
    /// State machine:
    ///   Idle → Scanning → Confirming → PlacingAnchor → Saving → Scanning
    ///                                                              ↓ (Done button)
    ///                                                          Uploading → Complete
    /// </summary>
    public class ScanPhaseManager : MonoBehaviour
    {
        // ── Inspector wiring ─────────────────────────────────────────────────
        [Header("Core Components")]
        [SerializeField] private OCRScanController      _ocr;
        [SerializeField] private SpatialAnchorController _anchors;
        [SerializeField] private DataSyncManager         _sync;

        [Header("Panels — enable/disable whole GameObjects per state")]
        [SerializeField] private GameObject _panelIdle;
        [SerializeField] private GameObject _panelScanning;
        [SerializeField] private GameObject _panelConfirm;
        [SerializeField] private GameObject _panelUploading;
        [SerializeField] private GameObject _panelComplete;

        [Header("Confirm Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _confirmSerialText;
        [SerializeField] private TMPro.TextMeshProUGUI _confirmMacText;
        [SerializeField] private Button                _btnConfirmAccept;
        [SerializeField] private Button                _btnConfirmRescan;

        [Header("Scanning Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _scanCounterText;   // "12 scanned"
        [SerializeField] private TMPro.TextMeshProUGUI _scanStatusText;    // "Searching…"
        [SerializeField] private Button                _btnDoneScanning;

        [Header("Uploading Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _uploadStatusText;

        // ── State ────────────────────────────────────────────────────────────
        public enum Phase { Idle, Scanning, Confirming, PlacingAnchor, Saving, Uploading, Complete }
        public Phase CurrentPhase { get; private set; } = Phase.Idle;

        private ScanSession _session;
        private string _pendingSerial;
        private string _pendingMac;

        // ── Lifecycle ────────────────────────────────────────────────────────
        private void Awake()
        {
            _ocr.OnDeviceConfirmed += OnDeviceConfirmed;
            _btnConfirmAccept?.onClick.AddListener(OnConfirmAccepted);
            _btnConfirmRescan?.onClick.AddListener(OnConfirmRejected);
            _btnDoneScanning?.onClick.AddListener(OnDoneScanningPressed);
        }

        private void Start()
        {
            // Resume an interrupted session if one exists locally
            var saved = _sync.LoadSessionLocally();
            _session = saved ?? new ScanSession();

            SetPhase(Phase.Idle);
        }

        // ── Public UI entry points (wire to buttons in Inspector) ────────────

        /// <summary>Called by the "Start Scanning" button on the Idle panel.</summary>
        public void OnStartPressed()
        {
            SetPhase(Phase.Scanning);
        }

        // ── State transitions ────────────────────────────────────────────────
        private void SetPhase(Phase next)
        {
            CurrentPhase = next;

            _panelIdle?.SetActive(next == Phase.Idle);
            _panelScanning?.SetActive(next == Phase.Scanning || next == Phase.PlacingAnchor || next == Phase.Saving);
            _panelConfirm?.SetActive(next == Phase.Confirming);
            _panelUploading?.SetActive(next == Phase.Uploading);
            _panelComplete?.SetActive(next == Phase.Complete);

            switch (next)
            {
                case Phase.Scanning:
                    _ocr.StartScanning();
                    RefreshCounter();
                    break;

                case Phase.Confirming:
                    _ocr.StopScanning();
                    if (_confirmSerialText) _confirmSerialText.text = $"S/N: {_pendingSerial}";
                    if (_confirmMacText)    _confirmMacText.text    = $"MAC: {_pendingMac}";
                    break;

                case Phase.PlacingAnchor:
                    PlaceAnchorForPending();
                    break;

                case Phase.Uploading:
                    _ocr.StopScanning();
                    StartCoroutine(UploadAndFinish());
                    break;
            }
        }

        // ── OCR callback ─────────────────────────────────────────────────────
        private void OnDeviceConfirmed(string serial, string mac)
        {
            _pendingSerial = serial;
            _pendingMac    = mac;
            SetPhase(Phase.Confirming);
        }

        // ── Confirm panel handlers ───────────────────────────────────────────
        private void OnConfirmAccepted()
        {
            SetPhase(Phase.PlacingAnchor);
        }

        private void OnConfirmRejected()
        {
            _pendingSerial = null;
            _pendingMac    = null;
            SetPhase(Phase.Scanning);
        }

        // ── Anchor placement ─────────────────────────────────────────────────
        private void PlaceAnchorForPending()
        {
            if (_scanStatusText) _scanStatusText.text = "Placing anchor…";

            var record = _session.AddRecord(_pendingSerial, _pendingMac);

            _anchors.PlaceAnchor(record, success =>
            {
                if (success)
                {
                    SaveAndContinue(record);
                }
                else
                {
                    // Anchor failed — keep the record but flag it, let user retry
                    Debug.LogWarning($"[ScanPhase] Anchor failed for {record.BoxId} — retrying scan.");
                    _session.Records.Remove(record);   // remove the incomplete record
                    SetPhase(Phase.Confirming);        // back to confirm so user can try again
                }
            });
        }

        // ── Save locally then resume scanning ────────────────────────────────
        private void SaveAndContinue(DeviceRecord record)
        {
            CurrentPhase = Phase.Saving;
            _sync.SaveSessionLocally(_session);

            if (_scanStatusText) _scanStatusText.text = $"Saved {record.BoxId} ✓";

            // Brief pause so the user can see the confirmation before the next scan
            StartCoroutine(ResumeAfterDelay(0.8f));
        }

        private IEnumerator ResumeAfterDelay(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            SetPhase(Phase.Scanning);
        }

        // ── Done button ──────────────────────────────────────────────────────
        private void OnDoneScanningPressed()
        {
            if (_session.TotalScanned == 0)
            {
                if (_scanStatusText) _scanStatusText.text = "Nothing scanned yet!";
                return;
            }
            SetPhase(Phase.Uploading);
        }

        // ── Upload flow ──────────────────────────────────────────────────────
        private IEnumerator UploadAndFinish()
        {
            SetUploadStatus("Uploading session…");
            bool done = false;

            _sync.UploadSession(_session, success =>
            {
                if (success)
                {
                    SetUploadStatus($"Done! {_session.TotalScanned} devices uploaded.\nStickers are printing…");
                    _sync.ClearLocalSession();          // clean slate for next job
                }
                else
                {
                    SetUploadStatus("Upload failed — check WiFi.\nSession saved locally. Try again.");
                }
                done = true;
            });

            yield return new WaitUntil(() => done);

            yield return new WaitForSeconds(2f);
            SetPhase(Phase.Complete);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void RefreshCounter()
        {
            if (_scanCounterText)
                _scanCounterText.text = $"{_session.TotalScanned} scanned";
        }

        private void SetUploadStatus(string msg)
        {
            if (_uploadStatusText) _uploadStatusText.text = msg;
        }

        private void OnDestroy()
        {
            if (_ocr) _ocr.OnDeviceConfirmed -= OnDeviceConfirmed;
        }
    }
}
