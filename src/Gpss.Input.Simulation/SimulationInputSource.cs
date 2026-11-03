using System.Collections.Immutable;
using Gpss.Core;
using Gpss.Input;

namespace Gpss.Input.Simulation;

/// <summary>Deterministic raw-value demo; no gamepad or native dependencies.</summary>
public sealed class SimulationInputSource : InputSourceBase
{
    public override BackendKind Kind => BackendKind.Simulation;
    public static DeviceInfo DemoDevice { get; } = new("demo", "デモ（左8bit / 右10bit）", BackendKind.Simulation,
        [new("lx", "左 X（8bit）", 0, 255, 8), new("ly", "左 Y（8bit）", 0, 255, 8),
         new("rx", "右 X（10bit）", 0, 1023, 10), new("ry", "右 Y（10bit）", 0, 1023, 10)]);
    private readonly Func<double, IReadOnlyDictionary<string, long>> _values;
    private readonly double _intervalMilliseconds;

    public SimulationInputSource(IMonotonicClock? clock = null,
        Func<double, IReadOnlyDictionary<string, long>>? values = null,
        double intervalMilliseconds = 1) : base(clock)
    {
        if (!double.IsFinite(intervalMilliseconds) || intervalMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        _values = values ?? DemoValues;
        _intervalMilliseconds = intervalMilliseconds;
    }
    public override Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>([DemoDevice]);
    }
    public static IReadOnlyDictionary<string, long> DemoValues(double seconds)
    {
        static long Triangle(double t, double speed, double phase, long maximum)
        {
            var position = 1.0 - 2.0 * Math.Abs(((t * speed + phase) % 1.0) - .5);
            // The triangle covers [0,1] over time; quantization uses actual integer range.
            var raw = Math.Round(position * maximum, MidpointRounding.AwayFromZero);
            return Math.Clamp((long)raw, 0, maximum);
        }
        return new Dictionary<string, long>
        {
            ["lx"] = Triangle(seconds, .07, .25, 255),
            ["ly"] = Triangle(seconds, .05, .40, 255),
            ["rx"] = Triangle(seconds, .06, .10, 1023),
            ["ry"] = Triangle(seconds, .04, .60, 1023),
        };
    }
    protected override async Task CaptureAsync(InputSession session, CancellationToken cancellationToken)
    {
        var bindings = session.Mapping.Bindings.IsEmpty
            ? new AxisMapping([new(Axis.LX, "lx"), new(Axis.LY, "ly"), new(Axis.RX, "rx"), new(Axis.RY, "ry")])
            : session.Mapping;
        foreach (var binding in bindings.Bindings)
            if (!session.Device.Axes.Any(a => a.Key == binding.DescriptorKey))
                throw new ArgumentException($"不明なデモ軸: {binding.DescriptorKey}");
        SetStatus(session.Epoch, ConnectionState.Connected, session.Device, "デモ入力を計測中");
        long sequence = 0;
        var start = Clock.Timestamp;
        while (!cancellationToken.IsCancellationRequested)
        {
            RecordApiCall(); RecordApiSuccess();
            var now = Clock.Timestamp;
            var values = _values((now - start) / (double)Clock.Frequency);
            var readings = ImmutableArray.CreateBuilder<AxisReading>();
            foreach (var binding in bindings.Bindings)
            {
                if (!values.TryGetValue(binding.DescriptorKey, out var raw)) continue;
                var descriptor = session.Device.Axes.First(a => a.Key == binding.DescriptorKey);
                if (raw < descriptor.LogicalMin || raw > descriptor.LogicalMax)
                { RecordParseError(); continue; }
                var normalized = AxisNormalization.Normalize(raw, descriptor.LogicalMin, descriptor.LogicalMax, binding.Inverted);
                readings.Add(new(binding.Axis, raw, normalized, 0));
            }
            PublishFrame(new(Kind, session.Epoch, sequence++, now, readings.ToImmutable(), "demo_quantized_v1", session.Settings.Mode));
            await Task.Delay(TimeSpan.FromMilliseconds(_intervalMilliseconds), cancellationToken).ConfigureAwait(false);
        }
    }
}
