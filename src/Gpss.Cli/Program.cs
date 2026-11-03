using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using Gpss.Application;
using Gpss.Core;
using Gpss.Input;
using Gpss.Input.HidDirect;
using Gpss.Input.Simulation;
using Gpss.Input.XInput;
using Gpss.Recording;

namespace Gpss.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await RunAsync(args); }
        catch (TimeoutException ex) { Console.Error.WriteLine($"タイムアウト: {ex.Message}"); return 3; }
        catch (OperationCanceledException) { Console.Error.WriteLine("測定をキャンセルしました。"); return 130; }
        catch (Exception ex) { Console.Error.WriteLine($"エラー: {ex.Message}"); return 2; }
    }

    public static async Task<int> RunAsync(string[] args, Func<BackendKind, IInputSource>? sourceFactory = null, CancellationToken cancellationToken = default)
    {
        if (args.Contains("--help") || args.Contains("-h")) { PrintHelp(); return 0; }
        var options = Options.Parse(args);
        if (options.AnalyzePath is not null)
        {
            var result = CsvRecording.Analyze(options.AnalyzePath, options.Axis);
            Console.WriteLine($"axis={options.Axis.ToString().ToLowerInvariant()} samples={result.Count} rows={result.Rows} duration_ms={result.DurationMs.ToString("F3", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"mean={Fmt(result.Mean)} stddev={Fmt(result.StdDev)} maxdiff={Fmt(result.MaxDeviation)}");
            return result.Count == 0 ? 1 : 0;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var userCancelled = false;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; userCancelled = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            await using var source = (sourceFactory ?? MakeSource)(options.Backend);
            var devices = await source.EnumerateAsync(cancellation.Token);
            if (devices.Count == 0)
            {
                if (options.ListOnly) { Console.WriteLine("入力機器は見つかりません。"); return 0; }
                throw new InvalidOperationException($"{options.Backend} の入力機器が見つかりません。");
            }
            foreach (var (device, i) in devices.Select((d, i) => (d, i))) Console.WriteLine($"[{i}] {device.Name} (id={device.Id})");
            if (options.ListOnly) return 0;
            if (options.Device is null && devices.Count > 1) throw new ArgumentException("複数の機器があります。--device ID を指定してください。");
            var selected = options.Device is null ? devices[0] : devices.FirstOrDefault(d => d.Id == options.Device || d.XInputSlot?.ToString() == options.Device)
                ?? throw new ArgumentException($"指定IDの機器がありません: {options.Device}");
            var loadedSettings = new SettingsStore().Load();
            if(loadedSettings.Warning is not null)Console.Error.WriteLine(loadedSettings.Warning);
            var mapping = SettingsStore.FindApplicableProfile(loadedSettings.Settings,selected) ?? SuggestedMapping.Create(selected);
            mapping = AddAuxiliaryBindings(selected, mapping, options);
            if (mapping.Bindings.IsEmpty && options.Backend != BackendKind.Simulation)
                throw new InvalidOperationException("4軸を一意に割り当てられない機器です。");
            var epoch = 1L;
            await source.StartAsync(selected, mapping, new(AcquisitionMode.LowLatency), epoch, cancellation.Token);
            var start = StopwatchClock.Instance.Timestamp;
            Console.WriteLine($"選択軸: {options.Axis.ToString().ToLowerInvariant()} / {options.Samples} サンプル");
            if (options.PrepareSeconds > 0)
            {
                Console.WriteLine($"準備中 ({options.PrepareSeconds} 秒)...");
                await CollectUntilAsync(source, start + (long)(options.PrepareSeconds * StopwatchClock.Instance.Frequency), cancellation.Token);
            }
            if (!options.NoWait && !Console.IsInputRedirected)
            {
                Console.WriteLine("Enterで測定開始");
                var line = Console.In.ReadLineAsync(cancellation.Token).AsTask();
                while (!line.IsCompleted)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Drain(source);
                    await Task.WhenAny(line, Task.Delay(10, cancellation.Token));
                    EnsureSourceHealthy(source);
                }
                await line;
            }
            Drain(source); // Discard every queued preparation/wait frame before establishing t=0.
            var measureStart = StopwatchClock.Instance.Timestamp;
            await using var csv = options.Output is null ? null : new StreamWriter(options.Output, false, new UTF8Encoding(false));
            if (csv is not null)
            {
                if (options.Format == "legacy") CsvRecording.WriteLegacyHeader(csv);
                else CsvRecording.WriteV2Header(csv);
                await csv.FlushAsync(cancellation.Token);
            }
            long count=0; double lastElapsedMs=0; double mean=0, m2=0, min=double.PositiveInfinity, max=double.NegativeInfinity;
            while (count < options.Samples)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var got = false;
                while (source.TryRead(out var frame))
                {
                    if (frame is null) continue; got = true;
                    var elapsed = frame.Timestamp - measureStart; if (elapsed < 0) continue;
                    lastElapsedMs=elapsed*1000d/StopwatchClock.Instance.Frequency;
                    EnsureSourceHealthy(source);
                    var values = Enum.GetValues<Axis>().ToDictionary(a=>a, _=>double.NaN);
                    foreach (var reading in frame.Readings) values[reading.Axis]=reading.Value;
                    var lt = AuxValue(frame, "lt"); var rt = AuxValue(frame, "rt");
                    if (csv is not null)
                    {
                        if (options.Format == "legacy") CsvRecording.WriteLegacyRow(csv, new(
                            (frame.Timestamp-start)*1000d/StopwatchClock.Instance.Frequency, elapsed*1000d/StopwatchClock.Instance.Frequency,
                            values[Axis.LX],values[Axis.LY],values[Axis.RX],values[Axis.RY],lt,rt));
                        else CsvRecording.WriteV2(csv,frame,elapsed,StopwatchClock.Instance.Frequency,source.Status.Device??selected,mapping);
                    }
                    foreach (var sample in frame.Readings)
                    {
                        if (sample.Axis != options.Axis || !double.IsFinite(sample.Value)) continue;
                        count++; var delta=sample.Value-mean; mean+=delta/count; m2+=delta*(sample.Value-mean);
                        min=Math.Min(min,sample.Value); max=Math.Max(max,sample.Value);
                        if (count>=options.Samples) break;
                    }
                    if (count >= options.Samples) break;
                }
                EnsureSourceHealthy(source);
                if (!got) await Task.Delay(1, cancellation.Token);
            }
            await source.StopAsync(CancellationToken.None);
            if(csv is not null) await csv.FlushAsync(CancellationToken.None);
            var std = Math.Sqrt(m2/count); var maxDeviation=Math.Max(Math.Abs(max-mean),Math.Abs(min-mean));
            Console.WriteLine($"samples={count} duration_ms={lastElapsedMs.ToString("F3",CultureInfo.InvariantCulture)} mean={Fmt(mean)} stddev={Fmt(std)} maxdiff={Fmt(maxDeviation)}");
            if (options.Output is not null) Console.WriteLine($"CSV: {Path.GetFullPath(options.Output)} ({options.Format})");
            return 0;
        }
        catch (OperationCanceledException) when (!userCancelled && !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("サンプル取得が指定時間内に完了しませんでした。"); }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task CollectUntilAsync(IInputSource source,long until,CancellationToken token)
    {
        while (StopwatchClock.Instance.Timestamp < until)
        { token.ThrowIfCancellationRequested(); Drain(source); EnsureSourceHealthy(source); await Task.Delay(4,token); }
    }
    private static void Drain(IInputSource source) { while(source.TryRead(out _)) { } }
    private static void EnsureSourceHealthy(IInputSource source)
    {
        if(source.Status.State is ConnectionState.Error or ConnectionState.Disconnected or ConnectionState.Unsupported)
            throw new IOException($"入力源 {source.Status.State}: {source.Status.Message}");
    }
    private static double AuxValue(InputFrame frame,string name)
    {
        if(frame.Auxiliary.IsDefaultOrEmpty) return double.NaN;
        foreach(var reading in frame.Auxiliary) if(reading.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) return reading.Value;
        return double.NaN;
    }
    private static AxisMapping AddAuxiliaryBindings(DeviceInfo device,AxisMapping mapping,Options options)
    {
        var aux = mapping.AuxiliaryBindings.IsDefaultOrEmpty ? new List<AuxiliaryBinding>() : new List<AuxiliaryBinding>(mapping.AuxiliaryBindings);
        void Add(string? key,string name)
        {
            if(key is null) return;
            var desc=device.Axes.FirstOrDefault(d=>d.Key==key);
            if(desc is null||!desc.Supported||!desc.IsAbsolute||desc.LogicalMax<=desc.LogicalMin) throw new ArgumentException($"{name} key is not a supported axis: {key}");
            aux.RemoveAll(a=>a.Name.Equals(name,StringComparison.OrdinalIgnoreCase)); aux.Add(new(name,key));
        }
        Add(options.LeftTriggerKey,"lt"); Add(options.RightTriggerKey,"rt");
        return mapping with { AuxiliaryBindings=aux.ToImmutableArray() };
    }
    private static IInputSource MakeSource(BackendKind backend) => backend switch
    { BackendKind.HidDirect => new HidDirectInputSource(), BackendKind.XInput => new XInputSource(), _ => new SimulationInputSource() };
    private static string Fmt(double n) => double.IsFinite(n) ? n.ToString("F6",CultureInfo.InvariantCulture) : "NaN";
    private static void PrintHelp() => Console.WriteLine("""
GPSS input measurement
--help --list --backend hid|xinput|demo --device ID --axis lx|ly|rx|ry
--samples N (default 10000) --prepare-seconds N --timeout-seconds N
--output PATH --format legacy|v2 --no-wait --analyze PATH --lt-key HID_AXIS --rt-key HID_AXIS

Legacy CSV: timestamps and elapsed_time are milliseconds; elapsed_time is monotonic
since measurement start; timestamps are milliseconds since input start. Missing values are NaN.
v2 columns are schema_version, timestamp_ms, elapsed_time_ms, backend, mode, decoder_id,
report_id_usage, epoch, sequence, dropped_count, record_type, continuity_id, axis,
raw_value, logical_min, logical_max, normalized_value, normalization, inverted,
link_collection and auxiliary name/raw/value/range. A frame with multiple readings uses
multiple rows with the same sequence; axis values are raw integers plus normalized values.
linear_minmax_v1 means 2*(raw-logical_min)/(logical_max-logical_min)-1, with the
recorded inversion flag applied. Missing axes/triggers remain NaN in legacy output.
""");
}

internal sealed record Options(BackendKind Backend, string? Device, Axis Axis, int Samples, double PrepareSeconds,
    double TimeoutSeconds, string? Output, string Format, bool NoWait, bool ListOnly, string? LeftTriggerKey, string? RightTriggerKey, string? AnalyzePath)
{
    public static Options Parse(string[] args)
    {
        var known=new HashSet<string>(["--help","-h","--list","--no-wait","--backend","--device","--axis","--samples","--prepare-seconds","--timeout-seconds","--output","--format","--analyze","--lt-key","--rt-key"]);
        for(var i=0;i<args.Length;i++)
        {
            if(!known.Contains(args[i])) throw new ArgumentException($"Unknown option: {args[i]}");
            if(args[i] is "--help" or "-h" or "--list" or "--no-wait") continue;
            if(i+1>=args.Length||args[i+1].StartsWith("--")) throw new ArgumentException($"{args[i]} needs a value");
            i++;
        }
        string? Get(string key) { var i=Array.IndexOf(args,key); if(i<0)return null; if(i+1>=args.Length||args[i+1].StartsWith("--"))throw new ArgumentException($"{key} needs a value");return args[i+1]; }
        bool Flag(string k)=>args.Contains(k);
        var backend=(Get("--backend")??"hid").ToLowerInvariant() switch { "hid"=>BackendKind.HidDirect,"xinput"=>BackendKind.XInput,"demo"=>BackendKind.Simulation,var s=>throw new ArgumentException($"Unknown backend: {s}") };
        var axis=(Get("--axis")??"rx").ToLowerInvariant() switch {"lx"=>Axis.LX,"ly"=>Axis.LY,"rx"=>Axis.RX,"ry"=>Axis.RY,var s=>throw new ArgumentException($"Unknown axis: {s}")};
        var samples=int.Parse(Get("--samples")??"10000",CultureInfo.InvariantCulture); var prep=double.Parse(Get("--prepare-seconds")??"10",CultureInfo.InvariantCulture); var timeout=double.Parse(Get("--timeout-seconds")??"300",CultureInfo.InvariantCulture);
        var format=(Get("--format")??"legacy").ToLowerInvariant(); if(format is not ("legacy" or "v2")) throw new ArgumentException("--format must be legacy or v2");
        if(samples<1||prep<0||timeout<=prep||!double.IsFinite(prep)||!double.IsFinite(timeout))throw new ArgumentOutOfRangeException("samples/prepare/timeout");
        return new(backend,Get("--device"),axis,samples,prep,timeout,Get("--output"),format,Flag("--no-wait"),Flag("--list"),Get("--lt-key"),Get("--rt-key"),Get("--analyze"));
    }
}
