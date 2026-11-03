using System.Globalization;

namespace Gpss.Core;

public static class StepPrecision
{
    public static bool TryGetBits(double difference, out double bits)
    {
        bits = 0;
        if (!double.IsFinite(difference) || difference <= 0 || difference > 2) return false;
        var levels = 2 / difference;
        if (!double.IsFinite(levels)) return false;
        bits = Math.Log2(levels);
        return double.IsFinite(bits);
    }

    public static double? MeanBits(StepEstimate? x, StepEstimate? y)
    {
        var nx = x?.Bits is double bx && double.IsFinite(bx) && x.NonzeroCount > 0 ? x.NonzeroCount : 0;
        var ny = y?.Bits is double by && double.IsFinite(by) && y.NonzeroCount > 0 ? y.NonzeroCount : 0;
        var total = (long)nx + ny;
        if (total == 0) return null;
        return (nx == 0 ? 0 : x!.Bits!.Value * (nx / (double)total)) +
               (ny == 0 ? 0 : y!.Bits!.Value * (ny / (double)total));
    }

    public static (double Levels, double Bits) Convert(double step)
    {
        if (!double.IsFinite(step) || step <= 0 || step > 2)
            throw new ArgumentOutOfRangeException(nameof(step), "Step must be finite and in (0, 2]");
        var levels = 2.0 / step;
        if (!double.IsFinite(levels))
            throw new ArgumentOutOfRangeException(nameof(step), "The number of levels cannot be represented");
        return (levels, Math.Log2(levels));
    }

    public static decimal Round(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    public static string FormatBits(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? Math.Round(value.Value, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture) + "bit"
        : "—";
}

public static class AxisNormalization
{
    public static double Normalize(long raw, long min, long max, bool inverted = false)
    {
        if (min >= max) throw new ArgumentOutOfRangeException(nameof(max), "Logical maximum must exceed minimum");
        if (raw < min || raw > max) throw new ArgumentOutOfRangeException(nameof(raw), "Axis value is outside logical range");
        // Ordered signed operands have non-negative differences representable in ulong.
        // Subtract before conversion so adjacent large integer bounds do not collapse.
        var offset = unchecked((ulong)raw - (ulong)min);
        var range = unchecked((ulong)max - (ulong)min);
        var normalized = 2.0 * offset / range - 1.0;
        return inverted ? -normalized : normalized;
    }
}
