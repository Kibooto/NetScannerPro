using System.Net;

namespace NetScannerPro.Models;

public class DeviceModel
{
    public IPAddress IPAddress { get; set; } = IPAddress.None;

    public string MACAddress { get; set; } = "N/A";

    public string Hostname { get; set; } = "Unknown";

    public string Vendor { get; set; } = "Unknown";

    public bool IsOnline { get; set; }

    public bool IsVideoDevice { get; set; }

    public string DeviceType { get; set; } = "Unknown";

    public DateTime? LastSeen { get; set; }
}
