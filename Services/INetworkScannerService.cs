using NetScannerPro.Models;

namespace NetScannerPro.Services;

public interface INetworkScannerService
{
    Task<IReadOnlyList<DeviceModel>> RunIntelligentScanAsync(
        string routerIp,
        string user,
        string pass,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceModel>> GetDevicesFromMikroTik(
        string ip,
        string user,
        string pass,
        CancellationToken cancellationToken = default);

    Task<bool> CheckIfVideoDeviceAsync(string ip, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceModel>> PerformDeepScanAsync(string baseIp, CancellationToken cancellationToken = default);
}
