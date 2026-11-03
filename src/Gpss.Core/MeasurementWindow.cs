using System.Collections.Immutable;

namespace Gpss.Core;

public static class MeasurementWindow
{
    public const int DefaultSeconds = 30;
    public static ImmutableArray<int> Options { get; } = [10, 30, 60, 300];
    public static bool IsSupported(int seconds) => Options.Contains(seconds);
    public static void Validate(int seconds)
    {
        if (!IsSupported(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), "Choose 10, 30, 60 or 300 seconds");
    }
    public static int SampleCapacity(int seconds)
    {
        Validate(seconds);
        return checked(seconds * 10_000 + 1);
    }
    public static string Label(int seconds) => seconds switch
    { 10 => "10秒", 30 => "30秒", 60 => "1分", 300 => "5分", _ => $"{seconds}秒" };
}
