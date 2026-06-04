namespace NetScannerPro.Services;

public class MacVendorService : IMacVendorService
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["4C:5E:0C"] = "MikroTik",
        ["F0:18:98"] = "Apple",
        ["3C:5A:B4"] = "Google",
        ["00:1A:11"] = "Samsung",
        ["B8:27:EB"] = "Raspberry Pi",
        ["FC:FB:FB"] = "Cisco"
    };

    public string ResolveVendor(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress) || macAddress.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        var normalized = NormalizeMac(macAddress);
        var prefix = normalized.Length >= 8 ? normalized[..8] : normalized;

        return Vendors.TryGetValue(prefix, out var vendor)
            ? vendor
            : "Unknown";
    }

    private static string NormalizeMac(string macAddress)
    {
        var cleaned = macAddress.Trim().ToUpperInvariant().Replace('-', ':');
        var parts = cleaned.Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3)
        {
            return $"{parts[0].PadLeft(2, '0')}:{parts[1].PadLeft(2, '0')}:{parts[2].PadLeft(2, '0')}";
        }

        return cleaned;
    }
}
