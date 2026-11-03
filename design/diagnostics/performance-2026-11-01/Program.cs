using Gpss.Core;
using Gpss.Application;
using Gpss.Input.XInput;
using System.Diagnostics;
using System.Runtime;
using System.Text.Json;

Console.WriteLine(JsonSerializer.Serialize(new { kind="environment", framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os=System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), serverGc=GCSettings.IsServerGC, cores=Environment.ProcessorCount, sampleBytes=System.Runtime.CompilerServices.Unsafe.SizeOf<AxisSample>() }));
if(args.Contains("--realtime")) { await RunRealtime(); return; }
foreach (var pattern in new[] { "quantized8", "noise16" })
foreach (var hz in new[] { 1000, 8000 })
foreach (var seconds in new[] { 1, 10, 30 })
{
    var clock=new Clock { Now=(long)seconds*1_000_000_000 };
    var windows=Enum.GetValues<Axis>().Select(a=>new AxisWindow(a,clock)).ToArray();
    var random=new Random(608);
    for(var i=0;i<=seconds*hz;i++)
    {
        for(var a=0;a<4;a++)
        {
            var raw=pattern=="quantized8"?(i/4+a*31)%256:random.Next(65536);
            var value=2d*raw/(pattern=="quantized8"?255:65535)-1;
            windows[a].Append(new(0,i,(long)i*clock.Frequency/hz,raw,value,0));
        }
    }
    var snapshots=windows.Select(w=>w.Snapshot(clock.Now)).ToArray();
    var estimator=new ModalStepEstimator();
    var stages=new Dictionary<string,Action> {
      ["snapshot"]=()=>{foreach(var w in windows)GC.KeepAlive(w.Snapshot(clock.Now));},
      ["projection"]=()=>{foreach(var s in snapshots)GC.KeepAlive(HistoryProjection.Reduce(s));},
      ["modal"]=()=>{foreach(var s in snapshots)GC.KeepAlive(estimator.Estimate(s.Differences.AsSpan()));},
      ["jitter"]=()=>{foreach(var s in snapshots)GC.KeepAlive(JitterCalculator.Calculate(s.Samples.Select(p=>p.Value)));},
      ["recentRate"]=()=>{foreach(var s in snapshots)GC.KeepAlive(s.Samples.Where(p=>p.Timestamp>=clock.Now-clock.Frequency).ToArray());},
      ["full"]=()=>{foreach(var w in windows){var s=w.Snapshot(clock.Now);GC.KeepAlive(HistoryProjection.Reduce(s));GC.KeepAlive(estimator.Estimate(s.Differences.AsSpan()));GC.KeepAlive(JitterCalculator.Calculate(s.Samples.Select(p=>p.Value)));GC.KeepAlive(s.Samples.Where(p=>p.Timestamp>=clock.Now-clock.Frequency).ToArray());}}
    };
    foreach(var (stage,action) in stages)
    {
        for(var i=0;i<5;i++) action();
        var timings=new List<double>();var bytes=GC.GetAllocatedBytesForCurrentThread();var g2=GC.CollectionCount(2);
        for(var i=0;i<20;i++){var start=Stopwatch.GetTimestamp();action();timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);}
        var allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;timings.Sort();
        Console.WriteLine(JsonSerializer.Serialize(new {kind="stage",pattern,hz,seconds,stage,pointsPerAxis=snapshots[0].Samples.Length, repetitions=20, bytesPerUpdate=allocated/20d, bytesPerSecondAt10Hz=allocated/2d, p50ms=timings[10],p95ms=timings[18],maxms=timings[^1],gen2Collections=GC.CollectionCount(2)-g2}));
    }
    GC.KeepAlive(windows);GC.KeepAlive(snapshots);
}
static async Task RunRealtime()
{
    var api=new NoisyApi(); await using var controller=new GpssController(_=>new XInputSource(api));
    controller.Start();await controller.SelectAsync(XInputSource.Device(0),settings:new AcquisitionSettings(AcquisitionMode.Normal,1));
    using var process=Process.GetCurrentProcess(); var started=Stopwatch.StartNew();var previousTime=0d;
    var allocated=GC.GetTotalAllocatedBytes();var cpu=process.TotalProcessorTime;var g2=GC.CollectionCount(2);var pause=GC.GetTotalPauseDuration();
    while(started.Elapsed.TotalSeconds<40)
    {
        await Task.Delay(5000);var snapshot=controller.Current;var now=started.Elapsed.TotalSeconds;var bytes=GC.GetTotalAllocatedBytes();var used=process.TotalProcessorTime;var gc2=GC.CollectionCount(2);var paused=GC.GetTotalPauseDuration();
        Console.WriteLine(JsonSerializer.Serialize(new {kind="realtime",scenario="mock XInput normal 1ms, independent random signed16 axes on every call",seconds=now,intervalSeconds=now-previousTime, observations=snapshot.Diagnostics.Observations,observationRate=snapshot.ObservationRate,apiCallRate=snapshot.ApiCallRate,samplesPerAxis=snapshot.Analysis?.Axes[Axis.LX].Count,retainedSeconds=snapshot.Analysis?.Axes[Axis.LX].RetainedSeconds,allocatedBytesPerSecond=(bytes-allocated)/(now-previousTime),cpuCorePercent=(used-cpu).TotalSeconds/(now-previousTime)*100,gen2Collections=gc2-g2,gcPauseMs=(paused-pause).TotalMilliseconds,snapshotAgeMs=Stopwatch.GetElapsedTime(snapshot.Now).TotalMilliseconds,analysisAgeMs=snapshot.Analysis is {} an?Stopwatch.GetElapsedTime(an.Timestamp).TotalMilliseconds:double.NaN,dropped=snapshot.Diagnostics.Dropped}));
        previousTime=now;allocated=bytes;cpu=used;g2=gc2;pause=paused;
    }
    await controller.StopAsync();
}
sealed class NoisyApi:IXInputApi
{
    private uint packet;private readonly Random random=new(608);
    public uint GetState(uint slot,out XInputState state)
    { state=new() {PacketNumber=++packet,Gamepad=new() {LX=(short)random.Next(-32768,32768),LY=(short)random.Next(-32768,32768),RX=(short)random.Next(-32768,32768),RY=(short)random.Next(-32768,32768)}};return 0; }
}
sealed class Clock:IMonotonicClock {public long Now;public long Timestamp=>Now;public long Frequency=>1_000_000_000;}
