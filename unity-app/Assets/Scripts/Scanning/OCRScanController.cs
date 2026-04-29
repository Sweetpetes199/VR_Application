using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using QSS.DeviceIntake.Data;

// Requires: Google ML Kit Text Recognition Unity plugin
// Package: com.google.mlkit.vision.text (added via External Dependency Manager)
// Docs: https://developers.google.com/ml-kit/vision/text-recognition/android

namespace QSS.DeviceIntake.Scanning
{
    public class OCRScanController : MonoBehaviour
    {
        [Header("Scan Settings")]
        [SerializeField] private float _scanIntervalSeconds = 0.5f;
        [SerializeField] private float _confirmationHoldSeconds = 1.5f; // how long result must be stable

        [Header("UI Feedback")]
        [SerializeField] private GameObject _scanReticle;
        [SerializeField] private TMPro.TextMeshProUGUI _extractedTextLabel;
        [SerializeField] private TMPro.TextMeshProUGUI _statusLabel;

        public event Action<string, string> OnDeviceConfirmed; // serial, mac
        public bool IsScanning { get; private set; }

        // Patterns — adjust to match your actual label format
        private static readonly Regex _serialPattern = new(@"S/N[:\s]*([A-Z0-9\-]{6,30})", RegexOptions.IgnoreCase);
        private static readonly Regex _macPattern    = new(@"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}");

        private string _lastSerial = "";
        private string _lastMac = "";
        private float _stableTimer = 0f;

        // ML Kit Java bridge references (Android only)
        private AndroidJavaObject _textRecognizer;
        private WebCamTexture _camTexture;

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            InitMLKit();
#endif
            StartWebCam();
        }

        private void InitMLKit()
        {
            using var recognizerOptions = new AndroidJavaObject(
                "com.google.mlkit.vision.text.latin.TextRecognizerOptions$Builder"
            );
            _textRecognizer = new AndroidJavaObject(
                "com.google.mlkit.vision.text.TextRecognition",
                recognizerOptions.Call<AndroidJavaObject>("build")
            );
        }

        private void StartWebCam()
        {
            // On Quest, passthrough handles the real feed.
            // WebCamTexture is used here for editor testing — swap for
            // OVRCameraRig passthrough texture on device if needed.
            if (WebCamTexture.devices.Length == 0) return;
            _camTexture = new WebCamTexture(WebCamTexture.devices[0].name, 1280, 720);
            _camTexture.Play();
        }

        public void StartScanning()
        {
            IsScanning = true;
            _scanReticle?.SetActive(true);
            StartCoroutine(ScanLoop());
        }

        public void StopScanning()
        {
            IsScanning = false;
            _scanReticle?.SetActive(false);
            StopAllCoroutines();
        }

        private IEnumerator ScanLoop()
        {
            while (IsScanning)
            {
                yield return new WaitForSeconds(_scanIntervalSeconds);
                RunOCRFrame();
            }
        }

        private void RunOCRFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            RunMLKitOCR();
#else
            // Editor stub — paste a test string to simulate a label scan
            SimulateEditorScan("S/N: SN-ABC123-XYZ\nMAC: AA:BB:CC:DD:EE:FF");
#endif
        }

        private void RunMLKitOCR()
        {
            if (_camTexture == null || !_camTexture.isPlaying) return;

            var pixels = _camTexture.GetPixels32();
            // Convert to byte[] JPEG and hand off to ML Kit via JNI
            // ML Kit callback is async — use AndroidJavaProxy for the listener
            var bitmap = TextureToBitmap(_camTexture);
            var image  = InputImageFromBitmap(bitmap);

            _textRecognizer.Call<AndroidJavaObject>("process", image)
                .Call<AndroidJavaObject>("addOnSuccessListener",
                    new MLKitSuccessListener(OnMLKitResult));
        }

        private void OnMLKitResult(string rawText)
        {
            ParseAndEvaluate(rawText);
        }

        private void SimulateEditorScan(string rawText)
        {
            ParseAndEvaluate(rawText);
        }

        private void ParseAndEvaluate(string rawText)
        {
            var serial = Extract(_serialPattern, rawText);
            var mac    = Extract(_macPattern, rawText);

            if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(mac))
            {
                ResetStableTimer();
                SetStatus("Searching...");
                return;
            }

            if (_extractedTextLabel)
                _extractedTextLabel.text = $"S/N: {serial}\nMAC: {mac}";

            if (serial == _lastSerial && mac == _lastMac)
            {
                _stableTimer += _scanIntervalSeconds;
                float pct = Mathf.Clamp01(_stableTimer / _confirmationHoldSeconds);
                SetStatus($"Hold steady... {pct * 100:F0}%");

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
            }
        }

        private void ResetStableTimer()
        {
            _stableTimer = 0f;
            _lastSerial  = "";
            _lastMac     = "";
        }

        private static string Extract(Regex pattern, string text)
        {
            var match = pattern.Match(text);
            return match.Success ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).Trim() : "";
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel) _statusLabel.text = msg;
        }

        // Stubs — implement with AndroidJavaObject calls matching ML Kit API
        private AndroidJavaObject TextureToBitmap(WebCamTexture tex) => null;
        private AndroidJavaObject InputImageFromBitmap(AndroidJavaObject bmp) => null;

        private void OnDestroy()
        {
            _camTexture?.Stop();
            _textRecognizer?.Dispose();
        }
    }

    // ML Kit async success callback bridge
    internal class MLKitSuccessListener : AndroidJavaProxy
    {
        private readonly Action<string> _callback;

        public MLKitSuccessListener(Action<string> callback)
            : base("com.google.android.gms.tasks.OnSuccessListener")
        {
            _callback = callback;
        }

        public void onSuccess(AndroidJavaObject result)
        {
            var text = result?.Call<string>("getText") ?? "";
            _callback?.Invoke(text);
        }
    }
}
