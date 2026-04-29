using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using QSS.DeviceIntake.Data;

// No third-party OCR plugin needed.
// Camera frames are JPEG-encoded and POSTed to the local PC server,
// which calls Claude Vision and returns serial + MAC as JSON.
// Server endpoint:  POST /api/ocr
// Request body:     { "ImageBase64": "<base64>", "MediaType": "image/jpeg" }
// Response body:    { "serial": "...", "mac": "...", "confidence": "high|medium|low" }

namespace QSS.DeviceIntake.Scanning
{
    public class OCRScanController : MonoBehaviour
    {
        [Header("Scan Settings")]
        [SerializeField] private float  _scanIntervalSeconds    = 1.0f;  // seconds between frames
        [SerializeField] private float  _confirmationHoldSeconds = 1.5f; // stable-hold time
        [SerializeField] private string _serverBaseUrl          = "http://192.168.1.100:5000";

        [Header("UI Feedback")]
        [SerializeField] private GameObject                _scanReticle;
        [SerializeField] private TMPro.TextMeshProUGUI    _extractedTextLabel;
        [SerializeField] private TMPro.TextMeshProUGUI    _statusLabel;

        public event Action<string, string> OnDeviceConfirmed; // serial, mac
        public bool IsScanning { get; private set; }

        // Stable-hold state
        private string _lastSerial = "";
        private string _lastMac    = "";
        private float  _stableTimer = 0f;
        private bool   _requestInFlight = false;

        private WebCamTexture _camTexture;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void Start()
        {
            StartWebCam();
        }

        private void StartWebCam()
        {
            if (WebCamTexture.devices.Length == 0)
            {
                SetStatus("No camera found");
                return;
            }
            _camTexture = new WebCamTexture(WebCamTexture.devices[0].name, 1280, 720);
            _camTexture.Play();
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void StartScanning()
        {
            IsScanning = true;
            _scanReticle?.SetActive(true);
            ResetStableTimer();
            StartCoroutine(ScanLoop());
        }

        public void StopScanning()
        {
            IsScanning = false;
            _scanReticle?.SetActive(false);
            StopAllCoroutines();
        }

        // ── Scan loop ────────────────────────────────────────────────────────

        private IEnumerator ScanLoop()
        {
            while (IsScanning)
            {
                yield return new WaitForSeconds(_scanIntervalSeconds);

                if (_requestInFlight) continue;  // don't pile up requests

#if UNITY_EDITOR
                // Editor stub — inject a fake label for regex-free testing
                yield return StartCoroutine(SimulateEditorScan(
                    "SN-ABC123-XYZ",
                    "AA:BB:CC:DD:EE:FF"
                ));
#else
                if (_camTexture != null && _camTexture.isPlaying)
                    yield return StartCoroutine(SendFrameToServer());
#endif
            }
        }

        // ── Send frame to Claude Vision via server ───────────────────────────

        private IEnumerator SendFrameToServer()
        {
            _requestInFlight = true;
            SetStatus("Scanning…");

            // Capture current frame and encode as JPEG
            var snapshot = new Texture2D(_camTexture.width, _camTexture.height, TextureFormat.RGB24, false);
            snapshot.SetPixels(_camTexture.GetPixels());
            snapshot.Apply();
            byte[] jpegBytes = snapshot.EncodeToJPG(75);
            Destroy(snapshot);

            string b64 = Convert.ToBase64String(jpegBytes);
            string json = $"{{\"ImageBase64\":\"{b64}\",\"MediaType\":\"image/jpeg\"}}";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            using var req = new UnityWebRequest($"{_serverBaseUrl}/api/ocr", "POST");
            req.uploadHandler   = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 15;

            yield return req.SendWebRequest();
            _requestInFlight = false;

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus($"Server error: {req.error}");
                yield break;
            }

            ProcessServerResponse(req.downloadHandler.text);
        }

        // ── Parse server JSON response ───────────────────────────────────────

        private void ProcessServerResponse(string jsonText)
        {
            OcrResponse resp;
            try
            {
                resp = JsonUtility.FromJson<OcrResponse>(jsonText);
            }
            catch (Exception ex)
            {
                SetStatus("Bad response from server");
                Debug.LogWarning($"[OCR] JSON parse failed: {ex.Message}\n{jsonText}");
                return;
            }

            if (resp == null || !string.IsNullOrEmpty(resp.error))
            {
                SetStatus("OCR error — check server log");
                Debug.LogWarning($"[OCR] Server returned error: {resp?.error}");
                ResetStableTimer();
                return;
            }

            string serial = (resp.serial ?? "").Trim();
            string mac    = (resp.mac    ?? "").Trim().ToUpper();

            if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(mac))
            {
                ResetStableTimer();
                SetStatus($"Searching… ({resp.confidence ?? "?"})");
                return;
            }

            EvaluateResult(serial, mac);
        }

        // ── Stable-hold logic (shared with editor path) ──────────────────────

        private void EvaluateResult(string serial, string mac)
        {
            if (_extractedTextLabel)
                _extractedTextLabel.text = $"S/N: {serial}\nMAC: {mac}";

            if (serial == _lastSerial && mac == _lastMac)
            {
                _stableTimer += _scanIntervalSeconds;
                float pct = Mathf.Clamp01(_stableTimer / _confirmationHoldSeconds);
                SetStatus($"Hold steady… {pct * 100:F0}%");

                if (_stableTimer >= _confirmationHoldSeconds)
                {
                    StopScanning();
                    OnDeviceConfirmed?.Invoke(serial, mac);
                }
            }
            else
            {
                _lastSerial = serial;
                _lastMac    = mac;
                ResetStableTimer();
                SetStatus($"Reading… S/N {serial}");
            }
        }

        // ── Editor stub ──────────────────────────────────────────────────────

        /// <summary>
        /// Used in Play mode on PC to test the stable-hold flow without a headset.
        /// Change the serial/mac arguments to match your actual label format.
        /// </summary>
        private IEnumerator SimulateEditorScan(string serial, string mac)
        {
            _requestInFlight = true;
            yield return new WaitForSeconds(0.1f);   // simulate tiny network delay
            _requestInFlight = false;
            EvaluateResult(serial, mac);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void ResetStableTimer()
        {
            _stableTimer = 0f;
            _lastSerial  = "";
            _lastMac     = "";
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel) _statusLabel.text = msg;
        }

        private void OnDestroy()
        {
            _camTexture?.Stop();
        }

        // ── Response DTO ─────────────────────────────────────────────────────

        [Serializable]
        private class OcrResponse
        {
            public string serial;
            public string mac;
            public string confidence;
            public string raw;
            public string error;
        }
    }
}
