using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace QSS.DeviceIntake.Data
{
    [Serializable]
    public class ScanSession
    {
        public string SessionId;
        public string CreatedAt;
        public List<DeviceRecord> Records = new();

        private int _boxCounter = 1;

        public ScanSession()
        {
            SessionId = Guid.NewGuid().ToString("N")[..8].ToUpper();
            CreatedAt = DateTime.UtcNow.ToString("o");
        }

        public DeviceRecord AddRecord(string serial, string mac)
        {
            var boxId = $"BOX-{_boxCounter:D3}";
            _boxCounter++;
            var record = new DeviceRecord(boxId, serial, mac);
            Records.Add(record);
            return record;
        }

        public DeviceRecord GetByBoxId(string boxId) =>
            Records.FirstOrDefault(r => r.BoxId == boxId);

        public DeviceRecord GetByAnchor(string anchorUuid) =>
            Records.FirstOrDefault(r => r.AnchorUuid == anchorUuid);

        public List<DeviceRecord> PendingStickers =>
            Records.Where(r => !r.StickerApplied && r.IsComplete).ToList();

        public int TotalScanned => Records.Count;
        public int TotalComplete => Records.Count(r => r.IsComplete);
        public int TotalStickered => Records.Count(r => r.StickerApplied);

        public string ToJson() => JsonUtility.ToJson(this, prettyPrint: true);

        public static ScanSession FromJson(string json) =>
            JsonUtility.FromJson<ScanSession>(json);
    }
}
