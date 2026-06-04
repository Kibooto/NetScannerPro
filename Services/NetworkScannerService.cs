using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetScannerPro.Models;
using tik4net;
using tik4net.Api;

namespace NetScannerPro.Services;

public class NetworkScannerService : INetworkScannerService
{
    private static readonly string[] VideoHostnameHints =
    [
        "IPC", "NVR", "HIK", "DAHUA", "CAMERA", "AMCREST"
    ];

    private static readonly string[] VideoVendors =
    [
        "Hikvision", "Dahua", "Zhejiang Dahua", "Hanwha", "Axis Communications"
    ];

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly Dictionary<string, string> LocalVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["14:A7:8B"] = "Apple",
        ["86:11:1D"] = "Apple",
        ["AE:2E:D5"] = "Apple",
        ["BC:60:A7"] = "Sony (PlayStation)",
        ["00:D9:D1"] = "Sony (PlayStation)",
        ["FC:03:9F"] = "Sony (PlayStation)",
        ["4C:5E:0C"] = "MikroTik",
        ["18:FD:74"] = "MikroTik",
        ["08:55:31"] = "MikroTik",
        ["00:1D:63"] = "Miele"
    };

    private static readonly Dictionary<string, string> VendorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object VendorCacheLock = new();

    public async Task<IReadOnlyList<DeviceModel>> RunIntelligentScanAsync(
        string routerIp,
        string user,
        string pass,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("1. Querying API...");
        var devices = (await GetDevicesFromMikroTik(routerIp, user, pass, cancellationToken)).ToList();

        progress?.Report("2. Pinging Subnet...");
        var pingDiscovered = await RunPingSweepAsync(routerIp, cancellationToken);

        foreach (var discovered in pingDiscovered)
        {
            if (devices.Any(d => d.IPAddress.Equals(discovered.IPAddress)))
            {
                continue;
            }

            devices.Add(discovered);
        }

        progress?.Report("3. Scanning Ports...");
        foreach (var device in devices.Where(d => d.IsOnline))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isVideo = await CheckIfVideoDeviceAsync(device.IPAddress.ToString(), cancellationToken);
            if (!isVideo)
            {
                continue;
            }

            device.IsVideoDevice = true;
            device.DeviceType = "CCTV Device";
            device.Vendor = "CCTV Equipment";

            if (ShouldOverrideHostnameForVideo(device))
            {
                device.Hostname = "Verified NVR / Camera";
            }
        }

        return devices
            .OrderBy(d => d.IPAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<DeviceModel>> GetDevicesFromMikroTik(
        string ip,
        string user,
        string pass,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new ArgumentException("Router IP, username and password are required.");
        }

        var devices = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using ITikConnection connection = ConnectionFactory.CreateConnection(TikConnectionType.Api);
                connection.Open(ip, user, pass);

                var arpRows = connection.CreateCommand("/ip/arp/print").ExecuteList() ?? [];
                var leaseRows = connection.CreateCommand("/ip/dhcp-server/lease/print").ExecuteList() ?? [];

                var hostByMac = leaseRows
                    .Select(row => new
                    {
                        Mac = NormalizeMac(GetResponseFieldSafe(row, "mac-address")),
                        Host = NormalizeHostname(GetResponseFieldSafe(row, "host-name"))
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Mac))
                    .GroupBy(x => x.Mac)
                    .ToDictionary(
                        x => x.Key,
                        x => x.Select(y => y.Host).FirstOrDefault(host => !string.IsNullOrWhiteSpace(host)) ?? "Unknown Device",
                        StringComparer.OrdinalIgnoreCase);

                var devices = new List<DeviceModel>();
                foreach (var arp in arpRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ipRaw = GetResponseFieldSafe(arp, "address");
                    var macRaw = NormalizeMac(GetResponseFieldSafe(arp, "mac-address"));
                    var isComplete = string.Equals(GetResponseFieldSafe(arp, "complete"), "true", StringComparison.OrdinalIgnoreCase);

                    if (!IPAddress.TryParse(ipRaw, out var parsedIp))
                    {
                        continue;
                    }

                    var hostname = "Unknown Device";
                    if (!string.IsNullOrWhiteSpace(macRaw) && hostByMac.TryGetValue(macRaw, out var mappedHostname))
                    {
                        hostname = NormalizeHostname(mappedHostname);
                    }

                    devices.Add(new DeviceModel
                    {
                        IPAddress = parsedIp,
                        MACAddress = string.IsNullOrWhiteSpace(macRaw) ? "N/A" : macRaw,
                        Hostname = hostname,
                        IsOnline = isComplete,
                        LastSeen = DateTime.UtcNow
                    });
                }

                return (IReadOnlyList<DeviceModel>)devices
                    .OrderBy(d => d.IPAddress.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                throw new InvalidOperationException(
                    "Unable to connect to MikroTik API (port 8728). Check router IP, credentials, and API service status.",
                    ex);
            }
        }, cancellationToken);

        await EnrichDevicesAsync(devices, cancellationToken);
        return devices;
    }

    public async Task<bool> CheckIfVideoDeviceAsync(string ip, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        var rtspTask = IsTcpPortOpenAsync(ip, 554, cancellationToken);
        var webTask = IsTcpPortOpenAsync(ip, 80, cancellationToken);
        var webSecureTask = IsTcpPortOpenAsync(ip, 443, cancellationToken);
        var hikTask = IsTcpPortOpenAsync(ip, 8000, cancellationToken);
        var dahuaTask = IsTcpPortOpenAsync(ip, 37777, cancellationToken);

        await Task.WhenAll(rtspTask, webTask, webSecureTask, hikTask, dahuaTask);

        var rtspOpen = rtspTask.Result;
        var webOpen = webTask.Result || webSecureTask.Result;
        var sdkOpen = hikTask.Result || dahuaTask.Result;

        return rtspOpen || (webOpen && sdkOpen);
    }

    public async Task<IReadOnlyList<DeviceModel>> PerformDeepScanAsync(string baseIp, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(baseIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Invalid router IP address.");
        }

        var bytes = parsed.GetAddressBytes();
        var subnetPrefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
        var range = Enumerable.Range(1, 254).Select(last => $"{subnetPrefix}.{last}");

        var discovered = new List<DeviceModel>();
        var sync = new object();

        await Parallel.ForEachAsync(range, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 128
        }, async (ip, ct) =>
        {
            if (!await IsHostOnlineAsync(ip, ct))
            {
                return;
            }

            var isVideo = await CheckIfVideoDeviceAsync(ip, ct);
            if (!IPAddress.TryParse(ip, out var ipAddress))
            {
                return;
            }

            var device = new DeviceModel
            {
                IPAddress = ipAddress,
                MACAddress = "N/A",
                Hostname = isVideo ? "Video Device" : "Unknown Device",
                Vendor = "Unknown",
                IsOnline = true,
                IsVideoDevice = isVideo,
                DeviceType = isVideo ? "CCTV Device" : "Unknown",
                LastSeen = DateTime.UtcNow
            };

            lock (sync)
            {
                discovered.Add(device);
            }
        });

        return discovered
            .OrderBy(d => d.IPAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsConnectionError(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("auth")
               || message.Contains("login")
               || message.Contains("connection")
               || message.Contains("socket")
               || message.Contains("refused")
               || message.Contains("timed out")
               || message.Contains("8728");
    }

    private static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return string.Empty;
        }

        return mac.Trim().Replace('-', ':').ToUpperInvariant();
    }

    private static string NormalizeHostname(string? hostname)
    {
        return string.IsNullOrWhiteSpace(hostname) ? "Unknown Device" : hostname.Trim();
    }

    private static async Task EnrichDevicesAsync(
        IReadOnlyList<DeviceModel> devices,
        CancellationToken cancellationToken)
    {
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vendor = await ResolveVendorByMacAsync(device.MACAddress, cancellationToken);
            device.Vendor = string.IsNullOrWhiteSpace(vendor) ? "Unknown" : vendor;

            if (IsUnknownHostname(device.Hostname) && !string.IsNullOrWhiteSpace(device.Vendor) && !IsUnknownHostname(device.Vendor))
            {
                device.Hostname = device.Vendor;
            }

            device.IsVideoDevice = IsVideoDevice(device.Hostname, device.Vendor);
            device.DeviceType = device.IsVideoDevice ? "CCTV Device" : "Unknown";
        }
    }

    private static bool IsUnknownHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return true;
        }

        return hostname.Equals("Unknown Device", StringComparison.OrdinalIgnoreCase)
               || hostname.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ResolveVendorByMacAsync(string? macAddress, CancellationToken cancellationToken)
    {
        var prefix = GetMacPrefix(macAddress);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "Unknown";
        }

        if (LocalVendors.TryGetValue(prefix, out var localVendor))
        {
            return localVendor;
        }

        lock (VendorCacheLock)
        {
            if (VendorCache.TryGetValue(prefix, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var requestUrl = $"https://api.macvendors.com/{Uri.EscapeDataString(macAddress!)}";
            var response = await HttpClient.GetAsync(requestUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return "Unknown";
            }

            var vendor = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(vendor))
            {
                return "Unknown";
            }

            lock (VendorCacheLock)
            {
                VendorCache[prefix] = vendor;
            }

            return vendor;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string GetMacPrefix(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return string.Empty;
        }

        var normalized = NormalizeMac(macAddress);
        return normalized.Length >= 8 ? normalized[..8] : string.Empty;
    }

    private static string? GetResponseFieldSafe(ITikReSentence sentence, string fieldName)
    {
        try
        {
            return sentence.GetResponseField(fieldName);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVideoDevice(string hostname, string vendor)
    {
        var host = hostname ?? string.Empty;
        var vendorName = vendor ?? string.Empty;

        var hostMatch = VideoHostnameHints.Any(hint =>
            host.Contains(hint, StringComparison.OrdinalIgnoreCase));

        var vendorMatch = VideoVendors.Any(knownVendor =>
            vendorName.Contains(knownVendor, StringComparison.OrdinalIgnoreCase));

        return hostMatch || vendorMatch;
    }

    private static async Task<List<DeviceModel>> RunPingSweepAsync(string baseIp, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(baseIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Invalid router IP address.");
        }

        var bytes = parsed.GetAddressBytes();
        var subnetPrefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
        var range = Enumerable.Range(1, 254).Select(last => $"{subnetPrefix}.{last}");

        var discovered = new List<DeviceModel>();
        var sync = new object();

        await Parallel.ForEachAsync(range, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 128
        }, async (ip, ct) =>
        {
            if (!await IsHostOnlineAsync(ip, ct))
            {
                return;
            }

            if (!IPAddress.TryParse(ip, out var ipAddress))
            {
                return;
            }

            var device = new DeviceModel
            {
                IPAddress = ipAddress,
                MACAddress = "N/A",
                Hostname = "Hidden Static Device",
                Vendor = "Unknown",
                IsOnline = true,
                IsVideoDevice = false,
                DeviceType = "Unknown",
                LastSeen = DateTime.UtcNow
            };

            lock (sync)
            {
                discovered.Add(device);
            }
        });

        return discovered;
    }

    private static bool ShouldOverrideHostnameForVideo(DeviceModel device)
    {
        if (IsUnknownHostname(device.Hostname))
        {
            return true;
        }

        if (device.Hostname.Equals(device.Vendor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostname = device.Hostname;
        return hostname.Equals("Apple", StringComparison.OrdinalIgnoreCase)
               || hostname.Equals("Sony (PlayStation)", StringComparison.OrdinalIgnoreCase)
               || hostname.Equals("MikroTik", StringComparison.OrdinalIgnoreCase)
               || hostname.Equals("Miele", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsHostOnlineAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 300);
            cancellationToken.ThrowIfCancellationRequested();
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsTcpPortOpenAsync(string ip, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(300));

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(ip, port, timeoutCts.Token);
            return tcpClient.Connected;
        }
        catch
        {
            return false;
        }
    }
}
