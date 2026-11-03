using System.Text.Json;
using System.Runtime.CompilerServices;
using Gpss.Core;

namespace Gpss.Core.Tests;

/// <summary>Cross-language regression from the unchanged Python v1 calculations.</summary>
public sealed class PythonParityTests
{
    [Fact]
    public void CSharpMatchesPythonExpectedValuesForSameNormalizedArrays()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FindFixture()));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var item in json.RootElement.GetProperty("jitter").EnumerateArray())
        {
            var result = JitterCalculator.Calculate(ReadArray(item, "values"));
            var expected = item.GetProperty("expected");
            Assert.Equal(expected.GetProperty("count").GetInt32(), result.Count);
            Near(expected.GetProperty("mean"), result.Mean);
            Near(expected.GetProperty("stddev"), result.PopulationStdDev);
            Near(expected.GetProperty("max_deviation"), result.MaxDeviation);
        }
        foreach (var item in json.RootElement.GetProperty("step").EnumerateArray())
        {
            var result = new ModalStepEstimator().Estimate(ReadArray(item, "differences"));
            var expected = item.GetProperty("expected");
            Near(expected.GetProperty("step"), result.Step);
            Near(expected.GetProperty("levels"), result.Levels);
            Near(expected.GetProperty("bits"), result.Bits);
            Assert.Equal(expected.GetProperty("nonzero_count").GetInt32(), result.NonzeroCount);
            Assert.Equal(expected.GetProperty("modal_count").GetInt32(), result.ModalCount);
            Assert.Equal(expected.GetProperty("excluded_count").GetInt32(), result.ExcludedCount);
            Assert.Equal(expected.GetProperty("tie").GetBoolean(), result.Tie);
            Near(expected.GetProperty("modal_ratio"), result.ModalRatio);
            Assert.Equal(expected.GetProperty("status").GetString() switch
            {
                "ok" => EstimateStatus.Valid,
                "reference" => EstimateStatus.Reference,
                "no_data" => EstimateStatus.NoData,
                _ => EstimateStatus.Invalid
            }, result.Status);
        }
    }

    private static double[] ReadArray(JsonElement parent, string name)
        => parent.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();

    private static void Near(JsonElement expected, double? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        var value = expected.GetDouble();
        Assert.True(Math.Abs(actual.Value - value) <= 1e-12 * Math.Max(1, Math.Abs(value)),
            $"Expected {value:R}, actual {actual:R}");
    }

    private static string FindFixture([CallerFilePath] string sourceFile = "")
    {
        var sourceRelative = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "fixtures", "core-parity-v1.json"));
        if (File.Exists(sourceRelative)) return sourceRelative;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "fixtures", "core-parity-v1.json");
            if (File.Exists(path)) return path;
            current = current.Parent;
        }
        throw new FileNotFoundException("Core parity fixture not found in repository tree");
    }
}
