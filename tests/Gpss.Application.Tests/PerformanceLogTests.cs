using System.Text.Json;
using Gpss.Desktop;

namespace Gpss.Application.Tests;

public sealed class PerformanceLogTests
{
    [Fact]
    public async Task RowsAreFlushedWhileRunningAndAppendIsDrainedAtClose()
    {
        var path=Path.Combine(Path.GetTempPath(),$"gpss-perf-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using(var writer=new PerformanceJsonlWriter(path))
            {
                Assert.True(writer.TryWrite(new { value="日本語", sequence=1 }));
                var deadline=DateTime.UtcNow.AddSeconds(5);
                while(!File.Exists(path)||File.ReadAllLines(path).Length!=1)
                { Assert.True(DateTime.UtcNow<deadline,"Live performance row was not flushed");await Task.Delay(10); }
                Assert.Null(writer.Failure);
            }
            await using(var writer=new PerformanceJsonlWriter(path))
                Assert.True(writer.TryWrite(new { value="next", sequence=2 }));
            var lines=File.ReadAllLines(path);Assert.Equal(2,lines.Length);
            using var first=JsonDocument.Parse(lines[0]);using var second=JsonDocument.Parse(lines[1]);
            Assert.Equal("日本語",first.RootElement.GetProperty("value").GetString());
            Assert.Equal(2,second.RootElement.GetProperty("sequence").GetInt32());
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task SlowDiskConsumerCannotAccumulateUnboundedPendingRows()
    {
        var path=Path.Combine(Path.GetTempPath(),$"gpss-perf-{Guid.NewGuid():N}.jsonl");
        using var blocker=new BlockingRow();
        try
        {
            await using(var writer=new PerformanceJsonlWriter(path))
            {
                try
                {
                    Assert.True(writer.TryWrite(blocker));
                    Assert.True(await Task.Run(()=>blocker.Entered.Wait(TimeSpan.FromSeconds(3))));
                    var accepted=0;
                    for(var i=0;i<100;i++)if(writer.TryWrite(new { sequence=i }))accepted++;
                    Assert.Equal(8,accepted);Assert.Equal(92,writer.DroppedRows);
                }
                finally { blocker.Release.Set(); }
            }
            Assert.Equal(9,File.ReadAllLines(path).Length);
        }
        finally { blocker.Release.Set();File.Delete(path); }
    }
    [Fact]
    public async Task InvalidOutputReportsFailureAndCloseRemainsSafe()
    {
        var path=Path.Combine(Path.GetTempPath(),$"gpss-perf-dir-{Guid.NewGuid():N}");Directory.CreateDirectory(path);
        try
        {
            var writer=new PerformanceJsonlWriter(path);
            writer.TryWrite(new { value=1 });await writer.DisposeAsync();
            Assert.NotNull(writer.Failure);Assert.False(writer.TryWrite(new { value=2 }));
        }
        finally { Directory.Delete(path); }
    }
    [Fact]
    public async Task SizeLimitStopsAppendingWithoutGrowingTheFile()
    {
        var path=Path.Combine(Path.GetTempPath(),$"gpss-perf-cap-{Guid.NewGuid():N}.jsonl");
        const long maximum=16L*1024*1024;
        try
        {
            using(var file=File.Create(path))file.SetLength(maximum-1);
            var writer=new PerformanceJsonlWriter(path);Assert.True(writer.TryWrite(new { value=1 }));
            await writer.DisposeAsync();Assert.Contains("limit",writer.Failure);
            Assert.Equal(maximum-1,new FileInfo(path).Length);
            using(var file=File.OpenWrite(path))file.SetLength(maximum+1);
            Assert.Throws<IOException>(()=>new PerformanceJsonlWriter(path));
        }
        finally { File.Delete(path); }
    }
    public sealed class BlockingRow : IDisposable
    {
        public readonly ManualResetEventSlim Entered=new(),Release=new();
        public string Value
        {
            get { Entered.Set();if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();return "released"; }
        }
        public void Dispose() { Entered.Dispose();Release.Dispose(); }
    }
}
