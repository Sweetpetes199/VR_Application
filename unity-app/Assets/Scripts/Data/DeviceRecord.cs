using System;
using UnityEngine;

namespace QSS.DeviceIntake.Data
{
    [Serializable]
    public class DeviceRecord
    {
        public string BoxId;           // e.g. "BOX-001" — links sticker to spatial anchor
        public string SerialNumber;
        public string MacAddress;
        public string RoomName;
        public string PassFail;        // "PASS" | "FAIL" | "PENDING"
        public string AnchorUuid;      // Meta OVRSpatialAnchor UUID
        public string ScannedAt;       // ISO 8601 timestamp
        public bool StickerApplied;

        // Populated after server sync
        public string PrintJobId;

        public DeviceRecord(string boxId, string serial, string mac)
        {
            BoxId = boxId;
            SerialNumber = serial;
            MacAddress = mac;
            PassFail = "PENDING";
            StickerApplied = false;
            ScannedAt = DateTime.UtcNow.ToString("o");
        }

        public bool IsComplete => !string.IsNullOrEmpty(SerialNumber)
                               && !string.IsNullOrEmpty(MacAddress)
                               && !string.IsNullOrEmpty(AnchorUuid);
    }
}
