using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using QSS.DeviceIntake.Anchors;
using QSS.DeviceIntake.Data;
using QSS.DeviceIntake.Navigation;
using QSS.DeviceIntake.Sync;

namespace QSS.DeviceIntake
{
    /// <summary>
    /// Phase 3 scene orchestrator.
    /// Drop this on a persistent GameObject in ApplyPhase.unity and wire
    /// up the Inspector references — no other glue code needed.
    ///
    /// State machine:
    ///   Idle → FetchingResults → LoadingAnchors → Navigating → Confirming
    ///                                                 ↑___________↓  (loops per box)
    ///                                                          Complete
    /// </summary>
    public class ApplyPhaseManager : MonoBehaviour
    {
        // ── Inspector wiring ─────────────────────────────────────────────────
        [Header("Core Components")]
        [SerializeField] private NavigationController  _navigation;
        [SerializeField] private SpatialAnchorController _anchors;
        [SerializeField] private DataSyncManager        _sync;

        [Header("Panels")]
        [SerializeField] private GameObject _panelIdle;
        [SerializeField] private GameObject _panelLoading;
        [SerializeField] private GameObject _panelNavigating;
        [SerializeField] private GameObject _panelConfirm;
        [SerializeField] private GameObject _panelComplete;

        [Header("Loading Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _loadingStatusText;

        [Header("Navigating Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _navProgressText;   // "4 / 12 remaining"
        [SerializeField] private TMPro.TextMeshProUGUI _navBoxInfoText;    // current target info

        [Header("Confirm Panel UI — shown when user reaches the box")]
        [SerializeField] private TMPro.TextMeshProUGUI _confirmBoxIdText;
        [SerializeField] private TMPro.TextMeshProUGUI _confirmDetailsText;
        [SerializeField] private Button                _btnStickerPlaced;
        [SerializeField] private Button                _btnSkipBox;

        [Header("Complete Panel UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _completeStatsText;

        // ── State ────────────────────────────────────────────────────────────
        public enum Phase { Idle, FetchingResults, LoadingAnchors, Navigating, Confirming, Complete }
        public Phase CurrentPhase { get; private set; } = Phase.Idle;

        private ScanSession _session;

        // ── Lifecycle ────────────────────────────────────────────────────────
        private void Awake()
        {
            _btnStickerPlaced?.onClick.AddListener(OnStickerPlaced);
            _btnSkipBox?.onClick.AddListener(OnSkipBox);
        }

        private void Start()
        {
            _session = _sync.LoadSessionLocally();
            SetPhase(Phase.Idle);
        }

        // ── Public UI entry points ───────────────────────────────────────────

        /// <summary>Called by the "Begin Sticker Run" button on the Idle panel.</summary>
        public void OnBeginPressed()
        {
            if (_session == null || _session.TotalComplete == 0)
            {
                SetLoadingStatus("No session found.\nComplete a scan run first.");
                return;
            }
            SetPhase(Phase.FetchingResults);
        }

        // ── State machine ────────────────────────────────────────────────────
        private void SetPhase(Phase next)
        {
            CurrentPhase = next;

            _panelIdle?.SetActive(next == Phase.Idle);
            _panelLoading?.SetActive(next == Phase.FetchingResults || next == Phase.LoadingAnchors);
            _panelNavigating?.SetActive(next == Phase.Navigating);
            _panelConfirm?.SetActive(next == Phase.Confirming);
            _panelComplete?.SetActive(next == Phase.Complete);

            switch (next)
            {
                case Phase.FetchingResults:
                    StartCoroutine(FetchResultsThenLoad());
                    break;

                case Phase.LoadingAnchors:
                    SetLoadingStatus("Locating boxes…");
                    _anchors.LoadAnchorsForSession(_session, OnAnchorsLoaded);
                    break;

                case Phase.Navigating:
                    _navigation.BeginNavigation(_session);
                    RefreshNavProgress();
                    break;

                case Phase.Complete:
                    ShowCompleteStats();
                    break;
            }
        }

        // ── Fetch pass/fail results from server ──────────────────────────────
        private IEnumerator FetchResultsThenLoad()
        {
            SetLoadingStatus("Fetching results from server…");
            bool done = false;

            _sync.FetchResults(_session, success =>
            {
                if (success)
                {
                    // Persist the updated results locally so Phase 3 survives offline
                    _sync.SaveSessionLocally(_session);
                    SetLoadingStatus("Results loaded ✓");
                }
                else
                {
                    // Non-fatal — carry on with whatever PassFail is already on the records
                    SetLoadingStatus("Server unreachable — using cached results.");
                }
                done = true;
            });

            yield return new WaitUntil(() => done);
            yield return new WaitForSeconds(0.6f);

            SetPhase(Phase.LoadingAnchors);
        }

        // ── Anchors loaded callback ───────────────────────────────────────────
        private void OnAnchorsLoaded()
        {
            var pending = _session.PendingStickers.Count;
            if (pending == 0)
            {
                SetPhase(Phase.Complete);
                return;
            }
            SetPhase(Phase.Navigating);
        }

        // ── User reached a box — show confirm panel ───────────────────────────
        /// <summary>
        /// Call this from a proximity trigger or a UI "I'm here" button
        /// once the user is standing next to the target box.
        /// NavigationController can also call this directly.
        /// </summary>
        public void OnUserReachedBox(DeviceRecord record)
        {
            if (_confirmBoxIdText)
                _confirmBoxIdText.text = record.BoxId;

            if (_confirmDetailsText)
                _confirmDetailsText.text =
                    $"Room:  {record.RoomName}\n" +
                    $"S/N:   {record.SerialNumber}\n" +
                    $"MAC:   {record.MacAddress}\n" +
                    $"Result: {record.PassFail}";

            SetPhase(Phase.Confirming);
        }

        // ── Confirm panel buttons ─────────────────────────────────────────────
        private void OnStickerPlaced()
        {
            _navigation.ConfirmStickerApplied();
            _sync.SaveSessionLocally(_session);     // checkpoint after every sticker
            RefreshNavProgress();

            // Check if queue is empty
            if (_session.PendingStickers.Count == 0)
                SetPhase(Phase.Complete);
            else
                SetPhase(Phase.Navigating);
        }

        private void OnSkipBox()
        {
            // Move to next without marking placed — user can come back later
            SetPhase(Phase.Navigating);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void RefreshNavProgress()
        {
            if (_navProgressText == null || _session == null) return;
            int remaining = _session.PendingStickers.Count;
            int total     = _session.TotalComplete;
            _navProgressText.text = $"{total - remaining} / {total} stickered";
        }

        private void SetLoadingStatus(string msg)
        {
            if (_loadingStatusText) _loadingStatusText.text = msg;
            Debug.Log($"[ApplyPhase] {msg}");
        }

        private void ShowCompleteStats()
        {
            if (_completeStatsText == null || _session == null) return;
            int total   = _session.TotalComplete;
            int passed  = 0;
            int failed  = 0;
            foreach (var r in _session.Records)
            {
                if (r.PassFail == "PASS") passed++;
                else if (r.PassFail == "FAIL") failed++;
            }
            _completeStatsText.text =
                $"Run complete!\n\n" +
                $"Boxes stickered:  {total}\n" +
                $"PASS: {passed}   FAIL: {failed}\n\n" +
                $"Session: {_session.SessionId}";
        }
    }
}
