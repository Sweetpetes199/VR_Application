using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using QSS.DeviceIntake.Data;

// Requires: Meta XR Core SDK (com.meta.xr.sdk.core)
// OVRSpatialAnchor is provided by that package.

namespace QSS.DeviceIntake.Anchors
{
    public class SpatialAnchorController : MonoBehaviour
    {
        [Header("Prefabs")]
        [SerializeField] private GameObject _anchorMarkerPrefab; // visible AR pin over each scanned box

        // uuid → instantiated anchor GameObject
        private readonly Dictionary<string, GameObject> _anchorObjects = new();

        // Called after OCR confirms a device — places an anchor at the headset's current look position
        public void PlaceAnchor(DeviceRecord record, Action<bool> onComplete)
        {
            StartCoroutine(CreateAnchorCoroutine(record, onComplete));
        }

        private IEnumerator CreateAnchorCoroutine(DeviceRecord record, Action<bool> onComplete)
        {
            // Spawn anchor at 1.5m in front of the user's gaze
            var anchorGo = new GameObject($"Anchor_{record.BoxId}");
            anchorGo.transform.position = GetPlacementPosition();
            anchorGo.transform.rotation = Quaternion.identity;

            var spatialAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();

            // Wait for the anchor to be localised by the runtime
            float timeout = 5f;
            float elapsed = 0f;
            while (!spatialAnchor.Created && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!spatialAnchor.Created)
            {
                Debug.LogWarning($"[SpatialAnchor] Failed to create anchor for {record.BoxId}");
                Destroy(anchorGo);
                onComplete?.Invoke(false);
                yield break;
            }

            // Persist the anchor so it survives app restart
            spatialAnchor.Save(OVRSpatialAnchor.StorageLocation.Local,
                (anchor, success) =>
                {
                    if (!success)
                    {
                        Debug.LogWarning($"[SpatialAnchor] Save failed for {record.BoxId}");
                        onComplete?.Invoke(false);
                        return;
                    }

                    record.AnchorUuid = anchor.Uuid.ToString();
                    _anchorObjects[record.AnchorUuid] = anchorGo;

                    if (_anchorMarkerPrefab)
                    {
                        var marker = Instantiate(_anchorMarkerPrefab, anchorGo.transform);
                        marker.GetComponentInChildren<TMPro.TextMeshPro>()?.SetText(record.BoxId);
                    }

                    onComplete?.Invoke(true);
                });
        }

        // Reload all anchors from a session (called at Phase 3 startup)
        public void LoadAnchorsForSession(ScanSession session, Action onAllLoaded)
        {
            StartCoroutine(LoadAnchorsCoroutine(session, onAllLoaded));
        }

        private IEnumerator LoadAnchorsCoroutine(ScanSession session, Action onAllLoaded)
        {
            int pending = session.Records.Count;
            if (pending == 0) { onAllLoaded?.Invoke(); yield break; }

            foreach (var record in session.Records)
            {
                if (string.IsNullOrEmpty(record.AnchorUuid)) { pending--; continue; }

                if (!Guid.TryParse(record.AnchorUuid, out var uuid)) { pending--; continue; }

                OVRSpatialAnchor.LoadUnboundAnchors(
                    new OVRSpatialAnchor.LoadOptions
                    {
                        StorageLocation = OVRSpatialAnchor.StorageLocation.Local,
                        Uuids = new List<Guid> { uuid }
                    },
                    unboundAnchors =>
                    {
                        foreach (var unbound in unboundAnchors)
                        {
                            var anchorGo = new GameObject($"Anchor_{record.BoxId}");
                            var spatialAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();
                            unbound.BindTo(spatialAnchor);

                            _anchorObjects[record.AnchorUuid] = anchorGo;

                            if (_anchorMarkerPrefab && !record.StickerApplied)
                            {
                                var marker = Instantiate(_anchorMarkerPrefab, anchorGo.transform);
                                marker.GetComponentInChildren<TMPro.TextMeshPro>()?.SetText(record.BoxId);
                            }
                        }
                        pending--;
                        if (pending <= 0) onAllLoaded?.Invoke();
                    });
            }

            while (pending > 0) yield return null;
        }

        public Vector3? GetAnchorWorldPosition(string anchorUuid)
        {
            if (_anchorObjects.TryGetValue(anchorUuid, out var go))
                return go.transform.position;
            return null;
        }

        public void HideAnchorMarker(string anchorUuid)
        {
            if (_anchorObjects.TryGetValue(anchorUuid, out var go))
                go.SetActive(false);
        }

        private static Vector3 GetPlacementPosition()
        {
            var cam = Camera.main;
            if (cam == null) return Vector3.zero;
            return cam.transform.position + cam.transform.forward * 1.5f;
        }
    }
}
