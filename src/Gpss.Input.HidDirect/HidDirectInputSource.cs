using System.Collections.Immutable;
using System.Diagnostics;
using Gpss.Core;

namespace Gpss.Input.HidDirect;

public sealed class HidDirectInputSource : InputSourceBase, IInputDiscoveryDiagnostics
{
    private readonly IHidPlatform _platform;
    public override BackendKind Kind => BackendKind.HidDirect;
    public IReadOnlyList<string> DiscoveryErrors => _platform is WindowsHidPlatform windows ? windows.DiscoveryErrors : [];
    public HidDirectInputSource(IMonotonicClock? clock = null)
        : this(new WindowsHidPlatform(clock), clock) { }
    public HidDirectInputSource(IHidPlatform platform, IMonotonicClock? clock = null) : base(clock) => _platform = platform;
    public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return _platform.Enumerate(); }, cancellationToken);

    protected override async Task CaptureAsync(InputSession session, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _platform.Open(session.Device);
            var device = connection.Device; // Re-read descriptor on every connection epoch.
            if (!session.Device.Axes.OrderBy(a => a.Key, StringComparer.Ordinal)
                .SequenceEqual(device.Axes.OrderBy(a => a.Key, StringComparer.Ordinal)))
            {
                SetStatus(session.Epoch, ConnectionState.Unsupported, device, "HIDの軸情報が変更されました。再検索して割り当てを確認してください");
                return;
            }
            if (session.Mapping.Bindings.IsEmpty || session.Mapping.Bindings.Any(b =>
                !device.Axes.Any(a => a.Key == b.DescriptorKey && a.Supported)))
            {
                SetStatus(session.Epoch, ConnectionState.Unsupported, device, "軸の割当が必要です。診断欄で対応する4軸を選択してください");
                return;
            }
            if (session.Mapping.Bindings.Select(b => b.Axis).Distinct().Count() != session.Mapping.Bindings.Length)
                throw new ArgumentException("軸の割当が重複しています");
            SetStatus(session.Epoch, ConnectionState.Connected, device, "HID直接読み取りを計測中");
            long sequence = 0;
            var axisContinuity = Enum.GetValues<Axis>().ToDictionary(a => a, _ => 0L);
            var nextCatalogCheck = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                RecordApiCall();
                var result = await connection.ReadAsync(session.Settings, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    RecordApiSuccess();
                    var decoded = connection.Decode(result.Report.AsSpan(), session.Mapping, 0);
                    if (decoded.HadInvalidValues)
                    {
                        var specific = !decoded.InvalidAxes.IsDefaultOrEmpty;
                        RecordParseError(breakAllAxes: !specific);
                        if (specific) foreach (var axis in decoded.InvalidAxes.Distinct()) axisContinuity[axis]++;
                    }
                    var readings = decoded.Readings.Select(r => r with
                        { ContinuityId = r.ContinuityId + axisContinuity[r.Axis] }).ToImmutableArray();
                    // Count every received HID report, including reports without selected axes.
                    PublishFrame(new(Kind, session.Epoch, sequence++, result.Timestamp, readings,
                        connection.DecoderId, session.Settings.Mode, RawReport: result.Report, Auxiliary: decoded.Auxiliary));
                }
                // Bounded catalog rescan also detects removal during a silent/pending read.
                if (Stopwatch.GetElapsedTime(nextCatalogCheck).TotalSeconds >= 1)
                {
                    nextCatalogCheck = Stopwatch.GetTimestamp();
                    if (!_platform.Enumerate().Any(d => d.Id == device.Id))
                        throw new HidDeviceDisconnectedException("HID機器が取り外されました");
                }
            }
        }
        catch (HidDeviceDisconnectedException ex)
        {
            SetStatus(session.Epoch, ConnectionState.Disconnected, session.Device, ex.Message);
        }
    }
}
