using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Gpss.Desktop;

/// <summary>Bounded background JSONL sink. UI callers only perform TryWrite.</summary>
internal sealed class PerformanceJsonlWriter : IAsyncDisposable
{
    private const long MaxBytes=16L*1024*1024;
    private readonly Channel<object> _channel=Channel.CreateBounded<object>(new BoundedChannelOptions(8)
    { FullMode=BoundedChannelFullMode.Wait,SingleReader=true,SingleWriter=true });
    private readonly string _path;
    private readonly Task _worker;
    private long _writtenBytes;
    private long _droppedRows;
    private string? _failure;
    public string? Failure=>Volatile.Read(ref _failure);
    public long DroppedRows=>Interlocked.Read(ref _droppedRows);

    public PerformanceJsonlWriter(string path)
    {
        _path=Path.GetFullPath(path);
        _writtenBytes=File.Exists(_path)?new FileInfo(_path).Length:0;
        if(_writtenBytes>MaxBytes)throw new IOException("Performance log already exceeds the 16 MiB limit.");
        _worker=Task.Run(WriteLoopAsync);
    }

    public bool TryWrite(object value)
    {
        if(Failure is not null)return false;
        if(_channel.Writer.TryWrite(value))return true;
        Interlocked.Increment(ref _droppedRows);return false;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var directory=Path.GetDirectoryName(_path);
            if(!string.IsNullOrEmpty(directory))Directory.CreateDirectory(directory);
            await using var stream=new FileStream(_path,FileMode.Append,FileAccess.Write,FileShare.Read,4096,true);
            await foreach(var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var line=JsonSerializer.Serialize(item,item.GetType());
                var bytes=Encoding.UTF8.GetBytes(line+"\n");
                if(_writtenBytes+bytes.Length>MaxBytes)
                {
                    Interlocked.CompareExchange(ref _failure,"Performance log reached the 16 MiB limit; logging stopped.",null);
                    while(_channel.Reader.TryRead(out _)) { }
                    break;
                }
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                _writtenBytes+=bytes.Length;
            }
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            Interlocked.CompareExchange(ref _failure,$"Performance log write failed: {ex.Message}",null);
            while(_channel.Reader.TryRead(out _)) { }
        }
    }
}
