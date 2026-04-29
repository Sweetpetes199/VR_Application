using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using QSS.DeviceIntake.Data;

namespace QSS.DeviceIntake.Sync
{
    /// <summary>
    /// Sends completed scan sessions to the local PC server and retrieves
    /// pass/fail results once available.
    /// Server address is set in Project Settings → Player → Scripting Define
    /// or via the Inspector field below.
    /// </summary>
    public class DataSyncManager : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string _serverBaseUrl = "http://192.168.1.100:5000"; // local PC on same LAN

        [Header("Persistence")]
        private const string SaveKey = "ActiveScanSession";

        // ── Session Persistence ──────────────────────────────────────────────

        public void SaveSessionLocally(ScanSession session)
        {
            PlayerPrefs.SetString(SaveKey, session.ToJson());
            PlayerPrefs.Save();
        }

        public ScanSession LoadSessionLocally()
        {
            var json = PlayerPrefs.GetString(SaveKey, "");
            return string.IsNullOrEmpty(json) ? null : ScanSession.FromJson(json);
        }

        public void ClearLocalSession()
        {
            PlayerPrefs.DeleteKey(SaveKey);
            PlayerPrefs.Save();
        }

        // ── Network Sync ─────────────────────────────────────────────────────

        /// <summary>
        /// POST the full session to the server. Server writes Excel + queues print jobs.
        /// </summary>
        public void UploadSession(ScanSession session, Action<bool> onDone)
        {
            StartCoroutine(PostSession(session, onDone));
        }

        private IEnumerator PostSession(ScanSession session, Action<bool> onDone)
        {
            var url  = $"{_serverBaseUrl}/api/sessions";
            var body = Encoding.UTF8.GetBytes(session.ToJson());

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            var success = req.result == UnityWebRequest.Result.Success;
            if (!success)
                Debug.LogWarning($"[Sync] Upload failed: {req.error}");

            onDone?.Invoke(success);
        }

        /// <summary>
        /// Polls for pass/fail results and writes them back into session records.
        /// Call this at Phase 3 startup or on a timer.
        /// </summary>
        public void FetchResults(ScanSession session, Action<bool> onDone)
        {
            StartCoroutine(GetResults(session, onDone));
        }

        private IEnumerator GetResults(ScanSession session, Action<bool> onDone)
        {
            var url = $"{_serverBaseUrl}/api/sessions/{session.SessionId}/results";

            using var req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Sync] Fetch results failed: {req.error}");
                onDone?.Invoke(false);
                yield break;
            }

            try
            {
                var response = JsonUtility.FromJson<ResultsResponse>(req.downloadHandler.text);
                foreach (var result in response.Results)
                {
                    var record = session.GetByBoxId(result.BoxId);
                    if (record != null)
                    {
                        record.PassFail    = result.PassFail;
                        record.RoomName    = result.RoomName;
                        record.PrintJobId  = result.PrintJobId;
                    }
                }
                onDone?.Invoke(true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Sync] Parse error: {ex.Message}");
                onDone?.Invoke(false);
            }
        }

        [Serializable]
        private class ResultsResponse
        {
            public ResultItem[] Results;
        }

        [Serializable]
        private class ResultItem
        {
            public string BoxId;
            public string PassFail;
            public string RoomName;
            public string PrintJobId;
        }
    }
}
