namespace Gpss.Core;

public static class JitterCalculator
{
    public static AxisJitter Calculate(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = 0;
        var mean = 0d; var m2 = 0d;
        var min = double.PositiveInfinity; var max = double.NegativeInfinity;
        foreach (var value in values)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Statistics require finite values", nameof(values));
            count++; min = Math.Min(min, value); max = Math.Max(max, value);
            var delta = value - mean;
            mean += delta / count;
            m2 += delta * (value - mean);
        }
        if (count == 0) return new(0, null, null, null);
        return new(count, mean, Math.Sqrt(Math.Max(0, m2 / count)), Math.Max(Math.Abs(min - mean), Math.Abs(max - mean)));
    }
}

internal static class NumericSum
{
    public static double Compensated(IEnumerable<double> values)
    {
        double sum = 0, correction = 0;
        foreach (var value in values)
        {
            var next = sum + value;
            correction += Math.Abs(sum) >= Math.Abs(value)
                ? (sum - next) + value : (value - next) + sum;
            sum = next;
        }
        return sum + correction;
    }
}
