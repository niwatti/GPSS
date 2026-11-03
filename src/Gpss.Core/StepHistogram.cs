using System.Collections.Immutable;

namespace Gpss.Core;

public readonly record struct StepHistogramBin(int LowerBit, long Count);

/// <summary>One combined histogram for LX, LY, RX and RY; immutable across threads.</summary>
public sealed record StepHistogramSnapshot(
    long Epoch, long Timestamp, double? WindowSeconds,
    string ValueLabel, ImmutableArray<StepHistogramBin> Bins, long TotalCount);

/// <summary>Bounded counters for the bit values of actual, nonzero observed differences.</summary>
public sealed class StepHistogramCounts
{
    // StepPrecision requires a finite 2/delta, whose log2 is at most 1024 after rounding.
    private readonly long[] _counts = new long[1025];
    public long TotalCount { get; private set; }

    public void Clear() { Array.Clear(_counts); TotalCount = 0; }
    public void AddDifference(double difference) => Change(difference, 1);
    public void RemoveDifference(double difference) => Change(difference, -1);

    private void Change(double difference, int direction)
    {
        if (!StepPrecision.TryGetBits(difference, out var value)) return;
        var bits = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var bin = (int)Math.Floor(bits);
        if ((uint)bin >= _counts.Length) return;
        if (direction < 0 && _counts[bin] <= 0)
            throw new InvalidOperationException("Histogram edge expired before it was counted");
        _counts[bin] += direction; TotalCount += direction;
    }

    public void AddBins(ImmutableArray<StepHistogramBin> bins)
    {
        if (bins.IsDefaultOrEmpty) return;
        foreach (var bin in bins)
        {
            if ((uint)bin.LowerBit >= _counts.Length || bin.Count < 0)
                throw new ArgumentOutOfRangeException(nameof(bins));
            _counts[bin.LowerBit] += bin.Count; TotalCount += bin.Count;
        }
    }

    public ImmutableArray<StepHistogramBin> Snapshot()
    {
        var result = ImmutableArray.CreateBuilder<StepHistogramBin>();
        for (var bit = 0; bit < _counts.Length; bit++)
            if (_counts[bit] > 0) result.Add(new(bit, _counts[bit]));
        return result.ToImmutable();
    }
}
