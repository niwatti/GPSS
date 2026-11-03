using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Gpss.Core;
using Microsoft.Win32.SafeHandles;

namespace Gpss.Input.HidDirect;

/// <summary>Owns an outstanding native read and its memory until terminal completion.</summary>
internal sealed class WindowsHidConnection : IHidConnection
{
    private readonly SafeFileHandle _handle;
    private readonly SafeWaitHandle _event;
    private readonly HidMetadata _metadata;
    private readonly IMonotonicClock _clock;
    private readonly IHidReportDecoder? _decoder;
    private nint _buffer, _overlapped;
    private readonly int _length;
    private bool _pending, _disposed;
    public DeviceInfo Device => _metadata.Device;
    public string DecoderId => _decoder?.Id ?? "hid_capabilities_v1";

    public WindowsHidConnection(string path, IMonotonicClock clock, HidDecoderRegistry registry)
    {
        _clock = clock;
        _handle = WindowsHidPlatform.OpenHandle(path, reading: true);
        try
        {
            _metadata = HidMetadata.Load(_handle, path);
            _length = _metadata.Capabilities.InputReportByteLength;
            if (_length <= 0 || _length > 1024 * 1024) throw new IOException("HID input report長が無効です");
            _decoder = registry.Find(Device);
            _event = HidNative.CreateEventW(0, true, false, null);
            if (_event.IsInvalid) throw WindowsHidPlatform.Win32("HID完了イベントを作成できません");
            _buffer = Marshal.AllocHGlobal(_length);
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<HidOverlapped>());
            Marshal.StructureToPtr(new HidOverlapped { Event = _event.DangerousGetHandle() }, _overlapped, false);
        }
        catch
        {
            if (_overlapped != 0) Marshal.FreeHGlobal(_overlapped);
            if (_buffer != 0) Marshal.FreeHGlobal(_buffer);
            _event?.Dispose(); _metadata?.Dispose(); _handle.Dispose();
            throw;
        }
    }

    public Task<HidReadResult?> ReadAsync(AcquisitionSettings settings, CancellationToken cancellationToken)
        => Task.FromResult(Read(settings, cancellationToken)); // Isolated acquisition worker; never called on UI.

    private HidReadResult? Read(AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_pending)
        {
            if (!HidNative.ResetEvent(_event)) throw WindowsHidPlatform.Win32("ResetEventエラー");
            // Reset the native result fields as well as the event before reusing a completed request.
            Marshal.StructureToPtr(new HidOverlapped { Event = _event.DangerousGetHandle() }, _overlapped, false);
            var ok = HidNative.ReadFile(_handle, _buffer, (uint)_length, 0, _overlapped);
            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != HidNative.ErrorIoPending) throw ReadError(error);
            }
            _pending = true;
            if (ok) return Complete();
        }
        if (settings.Mode == AcquisitionMode.LowLatency && settings.BusyWaitMicroseconds > 0)
        {
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMicroseconds < settings.BusyWaitMicroseconds)
            {
                if (cancellationToken.IsCancellationRequested) { CancelAndDrain(); cancellationToken.ThrowIfCancellationRequested(); }
                if (HidNative.GetOverlappedResult(_handle, _overlapped, out var count, false)) return CopyCompleted(count);
                var error = Marshal.GetLastWin32Error();
                if (error != HidNative.ErrorIoIncomplete) { _pending = false; throw ReadError(error); }
                Thread.SpinWait(1);
            }
        }
        // Timeout leaves the pending request alive, allowing periodic removal checks without losing reports.
        for (int i = 0; i < 2; i++)
        {
            if (cancellationToken.IsCancellationRequested) { CancelAndDrain(); cancellationToken.ThrowIfCancellationRequested(); }
            var wait = HidNative.WaitForSingleObject(_event, 50);
            if (wait == HidNative.WaitObject0) return Complete();
            if (wait != HidNative.WaitTimeout) throw WindowsHidPlatform.Win32("HID完了待機エラー");
        }
        return null;
    }
    private HidReadResult Complete()
    {
        if (!HidNative.GetOverlappedResult(_handle, _overlapped, out var count, false))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != HidNative.ErrorIoIncomplete) _pending = false;
            throw ReadError(error);
        }
        return CopyCompleted(count);
    }
    private HidReadResult CopyCompleted(uint count)
    {
        var now = _clock.Timestamp;
        _pending = false;
        if (count == 0 || count > _length) throw new IOException("HID readの実長が無効です");
        var bytes = new byte[count];
        Marshal.Copy(_buffer, bytes, 0, bytes.Length);
        return new(now, bytes.ToImmutableArray());
    }
    private void CancelAndDrain()
    {
        if (!_pending) return;
        // Even ERROR_NOT_FOUND can race normal completion: always collect the terminal result.
        var cancelled = HidNative.CancelIoEx(_handle, _overlapped);
        var cancelError = cancelled ? 0 : Marshal.GetLastWin32Error();
        var success = HidNative.GetOverlappedResult(_handle, _overlapped, out _, true);
        var completionError = success ? 0 : Marshal.GetLastWin32Error();
        _pending = false;
        if (completionError is not (0 or HidNative.ErrorOperationAborted) && cancelError != HidNative.ErrorNotFound)
            throw ReadError(completionError);
    }

    public HidDecodeResult Decode(ReadOnlySpan<byte> report, AxisMapping mapping, long continuityId)
    {
        if (_decoder is not null) return _decoder.Decode(Device, report, mapping, continuityId);
        if (report.Length != _length) return new([], true);
        var bytes = report.ToArray();
        var readings = ImmutableArray.CreateBuilder<AxisReading>();
        bool invalid = false;
        var invalidAxes = ImmutableArray.CreateBuilder<Axis>();
        foreach (var binding in mapping.Bindings)
        {
            var descriptor = Device.Axes.FirstOrDefault(a => a.Key == binding.DescriptorKey);
            if (descriptor is null || !descriptor.Supported) { invalid = true; invalidAxes.Add(binding.Axis); continue; }
            // HID APIs include a leading zero byte for devices without report IDs.
            if (descriptor.ReportId != bytes[0]) continue;
            var status = HidNative.HidP_GetUsageValue(0, descriptor.UsagePage!.Value,
                descriptor.LinkCollection!.Value, descriptor.Usage!.Value,
                out var value, _metadata.Preparsed, bytes, (uint)bytes.Length);
            if (status != HidNative.HidpSuccess) { invalid = true; invalidAxes.Add(binding.Axis); continue; }
            var raw = HidDescriptorCodec.Interpret(value, descriptor);
            if (!HidDescriptorCodec.IsValid(raw, descriptor)) { invalid = true; invalidAxes.Add(binding.Axis); continue; }
            readings.Add(new(binding.Axis, raw,
                AxisNormalization.Normalize(raw, descriptor.LogicalMin, descriptor.LogicalMax, binding.Inverted), continuityId));
        }
        var auxiliary = ImmutableArray.CreateBuilder<AuxiliaryReading>();
        if (!mapping.AuxiliaryBindings.IsDefaultOrEmpty)
            foreach (var binding in mapping.AuxiliaryBindings)
            {
                var descriptor = Device.Axes.FirstOrDefault(a => a.Key == binding.DescriptorKey);
                if (descriptor is null || !descriptor.Supported || descriptor.ReportId != bytes[0]) continue;
                var code = HidNative.HidP_GetUsageValue(0, descriptor.UsagePage!.Value,
                    descriptor.LinkCollection!.Value, descriptor.Usage!.Value, out var value,
                    _metadata.Preparsed, bytes, (uint)bytes.Length);
                if (code != HidNative.HidpSuccess) continue;
                var raw = HidDescriptorCodec.Interpret(value, descriptor);
                if (!HidDescriptorCodec.IsValid(raw, descriptor)) continue;
                auxiliary.Add(new(binding.Name, raw,
                    AxisNormalization.Normalize(raw, descriptor.LogicalMin, descriptor.LogicalMax, binding.Inverted),
                    descriptor.LogicalMin, descriptor.LogicalMax));
            }
        return new(readings.ToImmutable(), invalid, invalidAxes.ToImmutable(), auxiliary.ToImmutable());
    }

    private static Exception ReadError(int error) => error is 1167 or 6 or 31
        ? new HidDeviceDisconnectedException($"HID接続が失われました: {error}")
        : new Win32Exception(error, $"HID readエラー: {error}");

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        // If a driver has not completed cancellation, this remains on the acquisition worker.
        // Never release the native buffers merely because a UI timeout expired.
        try { CancelAndDrain(); }
        finally
        {
            _disposed = true;
            if (_overlapped != 0) { Marshal.FreeHGlobal(_overlapped); _overlapped = 0; }
            if (_buffer != 0) { Marshal.FreeHGlobal(_buffer); _buffer = 0; }
            _event.Dispose(); _metadata.Dispose(); _handle.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
