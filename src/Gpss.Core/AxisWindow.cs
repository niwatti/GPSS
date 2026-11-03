using System.Collections.Immutable;

namespace Gpss.Core;

/// <summary>A bounded, event-driven window for exactly one axis.</summary>
public sealed class AxisWindow
{
    private readonly Axis _axis;
    private readonly long _frequency;
    private readonly long _duration;
    private readonly int _capacity;
    private readonly Queue<AxisSample> _samples = new();
    private readonly HashSet<long> _breaks = [];
    private long _epoch;
    private long? _lastSequence, _lastTimestamp, _lastTruncated;
    private long _dropped;
    private bool _breakNext;
    private long _localContinuity;

    public AxisWindow(Axis axis, IMonotonicClock clock, long epoch = 0,
        int capacity = 300000, double durationSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (clock.Frequency <= 0 || capacity <= 0 || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0 || durationSeconds * clock.Frequency > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Window limits must be positive and representable");
        _axis = axis;
        _frequency = clock.Frequency;
        _duration = checked((long)Math.Ceiling(durationSeconds * clock.Frequency));
        _capacity = capacity;
        Reset(epoch);
    }

    public void Reset(long epoch)
    {
        _epoch = epoch;
        _samples.Clear();
        _breaks.Clear();
        _lastSequence = _lastTimestamp = _lastTruncated = null;
        _dropped = 0;
        _breakNext = false;
        _localContinuity = 0;
    }

    public bool Append(AxisSample sample)
    {
        if (sample.Epoch != _epoch) return false;
        if (sample.Sequence < 0 || sample.Timestamp < 0
            || (_lastSequence.HasValue && sample.Sequence <= _lastSequence.Value)
            || (_lastTimestamp.HasValue && sample.Timestamp < _lastTimestamp.Value))
        {
            // An invalid edge is a boundary even when its next value is valid.
            if (!_lastSequence.HasValue || sample.Sequence > _lastSequence.Value)
                _lastSequence = sample.Sequence;
            _dropped++;
            _breakNext = true;
            return false;
        }
        _lastSequence = sample.Sequence;
        _lastTimestamp = sample.Timestamp;
        if (!double.IsFinite(sample.Value) || sample.Value < -1 || sample.Value > 1)
        {
            _dropped++;
            _breakNext = true;
            return false;
        }
        Expire(sample.Timestamp);
        if (_breakNext) _localContinuity++;
        if (_breakNext && _samples.Count != 0) _breaks.Add(sample.Sequence);
        _breakNext = false;
        _samples.Enqueue(sample with { ContinuityId = sample.ContinuityId + _localContinuity });
        if (_samples.Count > _capacity)
        {
            var removed = _samples.Dequeue();
            _lastTruncated = removed.Timestamp;
            _breaks.Remove(removed.Sequence);
            _dropped++;
        }
        return true;
    }

    public AxisWindowSnapshot Snapshot(long now)
    {
        Expire(now);
        var samples = _samples.Where(s => s.Timestamp <= now).ToImmutableArray();
        var differences = ImmutableArray.CreateBuilder<double>(Math.Max(0, samples.Length - 1));
        for (var i = 1; i < samples.Length; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            if (previous.ContinuityId == current.ContinuityId && !_breaks.Contains(current.Sequence))
                differences.Add(Math.Abs(current.Value - previous.Value));
        }
        var retained = samples.Length > 1
            ? (samples[^1].Timestamp - samples[0].Timestamp) / (double)_frequency : 0;
        var cutoff = Cutoff(now);
        return new(_epoch, _axis, now, _frequency, samples, differences.ToImmutable(),
            retained, _dropped, _lastTruncated.HasValue && _lastTruncated.Value >= cutoff,
            WindowSeconds: _duration / (double)_frequency);
    }

    private long Cutoff(long now) => now < long.MinValue + _duration ? long.MinValue : now - _duration;

    private void Expire(long now)
    {
        var cutoff = Cutoff(now);
        while (_samples.TryPeek(out var first) && first.Timestamp < cutoff)
        {
            _samples.Dequeue();
            _breaks.Remove(first.Sequence);
        }
        if (_samples.TryPeek(out var remaining)) _breaks.Remove(remaining.Sequence);
    }
}
