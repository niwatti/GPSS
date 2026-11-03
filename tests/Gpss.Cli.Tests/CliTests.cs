using Gpss.Core;
using Gpss.Input;
using Gpss.Input.Simulation;
using Gpss.Cli;

namespace Gpss.Cli.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("v2")]
    public async Task Demo_captures_finite_samples_and_streams_csv(string format)
    {
        var path=Path.GetTempFileName(); var saved=Console.Out;
        try
        {
            using var output=new StringWriter(); Console.SetOut(output);
            var exit=await Program.RunAsync(["--backend","demo","--samples","8","--prepare-seconds","0","--timeout-seconds","10","--no-wait","--output",path,"--format",format],
                _=>new SimulationInputSource(intervalMilliseconds:1));
            Assert.Equal(0,exit);
            var lines=File.ReadAllLines(path);
            Assert.True(lines.Length>1);
            Assert.Contains("samples=8",output.ToString());
            if(format=="legacy")
            {
                Assert.Equal("timestamps,elapsed_time,lx,ly,rx,ry,lt,rt",lines[0]);
                Assert.All(lines.Skip(1),line=>Assert.Equal(8,line.Split(',').Length));
                Assert.Contains(lines.Skip(1),line=>line.EndsWith(",NaN,NaN",StringComparison.Ordinal));
            }
            else
            {
                var width=lines[0].Split(',').Length;
                Assert.Equal(25,width); Assert.All(lines.Skip(1),line=>Assert.Equal(width,line.Split(',').Length));
                Assert.Contains("sequence",lines[0]); Assert.Contains("raw_value",lines[0]);
            }
        }
        finally { Console.SetOut(saved); File.Delete(path); }
    }

    [Fact]
    public async Task Unknown_option_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(()=>Program.RunAsync(["--backend","demo","--nope"]));
    }

    [Fact]
    public async Task Multiple_devices_require_an_explicit_selection()
    {
        var source=new StaticInputSource([SimulationInputSource.DemoDevice,SimulationInputSource.DemoDevice with { Id="second",Name="second" }]);
        await Assert.ThrowsAsync<ArgumentException>(()=>Program.RunAsync(["--backend","demo","--prepare-seconds","0","--timeout-seconds","2","--no-wait"],_=>source));
    }

    [Fact]
    public async Task Empty_source_times_out_with_a_distinct_error()
    {
        var source=new StaticInputSource([SimulationInputSource.DemoDevice]);
        await Assert.ThrowsAsync<TimeoutException>(()=>Program.RunAsync(["--backend","demo","--samples","1","--prepare-seconds","0","--timeout-seconds","0.1","--no-wait"],_=>source));
    }

    [Fact]
    public async Task Source_error_fails_fast_instead_of_becoming_a_timeout()
    {
        var source=new StaticInputSource([SimulationInputSource.DemoDevice],ConnectionState.Error);
        await Assert.ThrowsAsync<IOException>(()=>Program.RunAsync(["--backend","demo","--samples","1","--prepare-seconds","0","--timeout-seconds","10","--no-wait"],_=>source));
    }

    private sealed class StaticInputSource(IReadOnlyList<DeviceInfo> devices,ConnectionState startState=ConnectionState.Connected) : IInputSource
    {
        private InputStatus _status=new(0,ConnectionState.Searching);
        public BackendKind Kind=>BackendKind.Simulation;
        public InputStatus Status=>_status;
        public InputDiagnostics Diagnostics=>new();
        public Task<IReadOnlyList<DeviceInfo>> EnumerateAsync(CancellationToken cancellationToken=default)=>Task.FromResult(devices);
        public Task StartAsync(DeviceInfo device,AxisMapping mapping,AcquisitionSettings settings,long epoch,CancellationToken cancellationToken=default)
        { _status=new(epoch,startState,device,startState==ConnectionState.Error?"fixture error":""); return Task.CompletedTask; }
        public Task SelectDeviceAsync(DeviceInfo device,AxisMapping mapping,long epoch,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task SetModeAsync(AcquisitionSettings settings,long epoch,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task ResetAsync(long epoch,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public bool TryRead(out InputFrame? frame){frame=null;return false;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
