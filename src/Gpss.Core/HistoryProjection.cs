using System.Collections.Immutable;

namespace Gpss.Core;

/// <summary>Bounded visualization-only projection; statistics always use the original window.</summary>
public static class HistoryProjection
{
    public static AxisWindowSnapshot Reduce(AxisWindowSnapshot window, int pixelBuckets = 1024, int maximumPoints = 8192)
    {
        if (pixelBuckets < 1 || maximumPoints < 4) throw new ArgumentOutOfRangeException(nameof(pixelBuckets));
        if (!double.IsFinite(window.WindowSeconds) || window.WindowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(window));
        if (window.Samples.IsEmpty) return window with { Differences = [] };
        var samples = window.Samples;
        var output = ImmutableArray.CreateBuilder<AxisSample>(Math.Min(samples.Length, maximumPoints));
        var start = 0;
        var reducedContinuity = false;
        int Bucket(AxisSample s, int count) => Math.Clamp((int)((window.WindowSeconds + (s.Timestamp - window.Now) / (double)window.Frequency) / window.WindowSeconds * count), 0, count - 1);
        while (start < samples.Length)
        {
            var bucket = Bucket(samples[start], pixelBuckets);
            var continuity = samples[start].ContinuityId;
            var end = start + 1; var min = start; var max = start;
            while (end < samples.Length && samples[end].ContinuityId == continuity && Bucket(samples[end], pixelBuckets) == bucket)
            {
                if (samples[end].Value < samples[min].Value) min = end;
                if (samples[end].Value > samples[max].Value) max = end;
                end++;
            }
            if (!Append(output, samples[start], samples[min], samples[max], samples[end - 1], maximumPoints))
            {
                reducedContinuity = true;
                break;
            }
            start = end;
        }
        if (reducedContinuity)
        {
            // Preserve the entire time interval when segment boundaries exceed the budget.
            output.Clear();
            var buckets = Math.Min(pixelBuckets, maximumPoints / 4);
            AxisSample first = default, min = default, max = default, last = default;
            var currentBucket = -1;
            long run = 0, previousContinuity = samples[0].ContinuityId;
            foreach (var source in samples)
            {
                // Numeric continuity IDs can be reused after a gap. Give each run a unique ID
                // so dropping intervening runs cannot join observations across that gap.
                if (source.ContinuityId != previousContinuity) { run++; previousContinuity = source.ContinuityId; }
                var sample = source with { ContinuityId = run };
                var bucket = Bucket(sample, buckets);
                if (bucket != currentBucket)
                {
                    if (currentBucket >= 0) Append(output, first, min, max, last, maximumPoints);
                    first = min = max = sample;
                    currentBucket = bucket;
                }
                else
                {
                    if (sample.Value < min.Value) min = sample;
                    if (sample.Value > max.Value) max = sample;
                }
                last = sample;
            }
            Append(output, first, min, max, last, maximumPoints);
        }
        var projected = output.ToImmutable();
        return window with { Samples = projected, Differences = [],
            ProjectionTruncated = window.ProjectionTruncated || reducedContinuity,
            RetainedSeconds = projected.Length > 1 ? (projected[^1].Timestamp - projected[0].Timestamp) / (double)window.Frequency : 0 };
    }

    private static bool Append(ImmutableArray<AxisSample>.Builder output, AxisSample first, AxisSample min,
        AxisSample max, AxisSample last, int maximumPoints)
    {
        Span<AxisSample> selected = stackalloc AxisSample[] { first, min, max, last };
        for (var i = 1; i < selected.Length; i++)
        {
            var value = selected[i]; var j = i - 1;
            while (j >= 0 && selected[j].Sequence > value.Sequence) { selected[j + 1] = selected[j]; j--; }
            selected[j + 1] = value;
        }
        var count = 0; long previous = long.MinValue;
        foreach (var sample in selected)
        {
            if (sample.Sequence == previous) continue;
            previous = sample.Sequence; selected[count++] = sample;
        }
        if (output.Count + count > maximumPoints) return false;
        foreach (var sample in selected[..count]) output.Add(sample);
        return true;
    }
}
