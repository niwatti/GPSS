using Gpss.Core;

namespace Gpss.Core.Tests;

public sealed class LiveAxisWindowTests
{
    private sealed class Clock(long frequency = 1000) : IMonotonicClock
    {
        public long Timestamp => 0;
        public long Frequency => frequency;
    }

    private sealed class ProbeEstimator(string methodId, Action? onFirstAdd = null) : IIncrementalStepEstimator
    {
        private bool _firstAdd = true;
        private Action? OnFirstAdd { get; } = onFirstAdd;
        public string MethodId { get; } = methodId;
        public IStepEstimatorSession CreateSession() => new ProbeSession(this);
        public StepEstimate Estimate(ReadOnlySpan<double> differences) =>
            new(MethodId, differences.IsEmpty ? EstimateStatus.NoData : EstimateStatus.Reference,
                NonzeroCount: differences.Length);

        private sealed class ProbeSession(ProbeEstimator owner) : IStepEstimatorSession
        {
            private int _count;
            public string MethodId => owner.MethodId;
            public void Add(double difference)
            {
                if (owner._firstAdd)
                {
                    owner._firstAdd = false;
                    owner.OnFirstAdd?.Invoke();
                }
                _count++;
            }
            public void Remove(double difference) => _count--;
            public StepEstimate Estimate() => new(MethodId,
                _count == 0 ? EstimateStatus.NoData : EstimateStatus.Reference,
                NonzeroCount: _count);
        }
    }

    [Fact]
    public void RollingLiveAnalysisMatchesReferenceWindowAndCalculators()
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.LX, clock, epoch: 3, capacity: 1024, durationSeconds: 2);
        var live = new LiveAxisWindow(Axis.LX, clock, epoch: 3, capacity: 1024, durationSeconds: 2);
        var estimator = new ModalStepEstimator();
        var random = new Random(713);
        long time = 0;
        for (var sequence = 0; sequence < 6000; sequence++)
        {
            time += random.Next(0, 4); // Repeated timestamps remain valid observations.
            var value = Math.Round(random.NextDouble() * 2 - 1, 3);
            var continuity = sequence is >= 1700 and < 1710 || sequence is >= 4100 and < 4150 ? 1L : 0L;
            var sample = new AxisSample(3, sequence, time, sequence, value, continuity);
            Assert.Equal(reference.Append(sample), live.Append(sample));
            Assert.True(reference.Snapshot(time).Samples.Length > 0);
            if (sequence % 37 != 0 && sequence != 5999) continue;

            var snapshot = reference.Snapshot(time);
            var result = live.Analyze(time, estimator);
            Assert.True(result.Analysis.Count > 0);
            Assert.Equal(snapshot.Samples.Length, result.Analysis.Count);
            Assert.Equal(snapshot.Truncated, result.Analysis.Truncated);
            Assert.Equal(snapshot.DroppedCount, result.Analysis.DroppedCount);
            Assert.Equal(snapshot.RetainedSeconds, result.Analysis.RetainedSeconds, 12);
            long? expectedSequence = snapshot.Samples.IsEmpty ? null : snapshot.Samples[^1].Sequence;
            Assert.Equal(expectedSequence, result.Analysis.Latest?.Sequence);
            var jitter = JitterCalculator.Calculate(snapshot.Samples.Select(s => s.Value));
            Assert.Equal(jitter.Count, result.Analysis.Jitter.Count);
            if (jitter.Mean is double expectedMean)
            {
                Assert.True(Math.Abs(expectedMean - result.Analysis.Jitter.Mean!.Value) <= 1e-10);
                Assert.True(Math.Abs(jitter.PopulationStdDev!.Value - result.Analysis.Jitter.PopulationStdDev!.Value) <= 1e-9);
                Assert.Equal(jitter.MaxDeviation!.Value, result.Analysis.Jitter.MaxDeviation!.Value, 10);
            }
            else
            {
                Assert.Null(result.Analysis.Jitter.Mean);
                Assert.Null(result.Analysis.Jitter.PopulationStdDev);
                Assert.Null(result.Analysis.Jitter.MaxDeviation);
            }
            var expectedStep = estimator.Estimate(snapshot.Differences.AsSpan());
            Assert.Equal(expectedStep.Status, result.Analysis.Step.Status);
            Assert.Equal(expectedStep.NonzeroCount, result.Analysis.Step.NonzeroCount);
            Assert.Equal(expectedStep.ModalCount, result.Analysis.Step.ModalCount);
            Assert.Equal(expectedStep.ExcludedCount, result.Analysis.Step.ExcludedCount);
            Assert.Equal(expectedStep.Tie, result.Analysis.Step.Tie);
            Assert.Equal(expectedStep.ModalRatio, result.Analysis.Step.ModalRatio, 12);
            AssertOptionalDouble(expectedStep.Step, result.Analysis.Step.Step, 11);
        }
    }

    [Fact]
    public void ProjectionLimitIsDistinctFromAcquisitionTruncationAndHistoryKeepsShape()
    {
        var live = new LiveAxisWindow(Axis.RY, new Clock(), epoch: 8, capacity: 12000, durationSeconds: 30);
        for (var i = 0; i < 10000; i++)
        {
            var value = (i % 200 < 100 ? i % 100 : 100 - i % 100) / 100d;
            Assert.True(live.Append(new(8, i, i, i, value, 0)));
        }
        var result = live.Analyze(9999, new ModalStepEstimator());
        Assert.Equal(10000, result.Analysis.Count);
        Assert.False(result.Analysis.Truncated);
        Assert.False(result.History.ProjectionTruncated); // Expected first/min/max/last reduction is not data loss.
        Assert.False(result.History.Truncated);
        Assert.InRange(result.History.Samples.Length, 1, 8192);
        Assert.Equal(0, result.History.Samples[0].Sequence);
        Assert.Equal(9999, result.History.Samples[^1].Sequence);
        Assert.Contains(result.History.Samples, s => s.Value == 1);
        Assert.Contains(result.History.Samples, s => s.Value == 0);
    }

    [Fact]
    public void ProjectionOverflowKeepsTheWholeIntervalAndSignalsOmission()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), capacity: 10000, durationSeconds: 30);
        for (var i = 0; i < 9000; i++)
            Assert.True(live.Append(new(0, i, i, i, (i % 2) / 10d, i)));

        var result = live.Analyze(8999, new ModalStepEstimator());
        Assert.True(result.History.ProjectionTruncated);
        Assert.InRange(result.History.Samples.Length, 2, 4096);
        Assert.Equal(8999, result.History.Samples[^1].Sequence);
        Assert.Equal(0, result.History.Samples[0].Sequence);
        for (var i = 1; i < result.History.Samples.Length; i++)
            Assert.NotEqual(result.History.Samples[i - 1].ContinuityId, result.History.Samples[i].ContinuityId);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(8000)]
    public void FrequentContinuityChangesKeepTheWholeThirtySecondHistory(int rate)
    {
        const long frequency = 10_000_000, origin = 1_234_567_890_000;
        var live = new LiveAxisWindow(Axis.RY, new Clock(frequency), epoch: 19);
        var lastSequence = 45 * rate;
        for (var i = 0; i <= lastSequence; i++)
            Assert.True(live.Append(new(19, i, origin + i * (frequency / rate), i,
                (i % 101) / 50d - 1, i)));

        var now = origin + lastSequence * (frequency / rate);
        var result = live.Analyze(now, new ModalStepEstimator());
        Assert.Equal(30 * rate + 1, result.Analysis.Count);
        Assert.Equal(30, result.Analysis.RetainedSeconds);
        Assert.False(result.Analysis.Truncated);
        Assert.True(result.History.ProjectionTruncated);
        Assert.InRange(result.History.Samples.Length, 2, 8192);
        Assert.Equal(now - 30 * frequency, result.History.Samples[0].Timestamp);
        Assert.Equal(now, result.History.Samples[^1].Timestamp);
        Assert.Contains(result.History.Samples, p => p.Timestamp > now - 30 * frequency && p.Timestamp < now - 20 * frequency);
        Assert.Contains(result.History.Samples, p => p.Timestamp > now - 20 * frequency && p.Timestamp < now - 10 * frequency);
        Assert.Contains(result.History.Samples, p => p.Timestamp > now - 10 * frequency && p.Timestamp < now);
        Assert.Contains(result.History.Samples, p => p.Value == -1);
        Assert.Contains(result.History.Samples, p => p.Value == 1);
        for (var i = 1; i < result.History.Samples.Length; i++)
        {
            Assert.True(result.History.Samples[i - 1].Sequence < result.History.Samples[i].Sequence);
            Assert.NotEqual(result.History.Samples[i - 1].ContinuityId, result.History.Samples[i].ContinuityId);
        }
    }

    [Fact]
    public void CoarseHistoryHandlesVisiblePrefixesAndReturnsToNormalAfterExpiry()
    {
        var live = new LiveAxisWindow(Axis.LY, new Clock(), epoch: 20);
        var estimator = new ModalStepEstimator();
        for (var i = 0; i < 9000; i++)
            Assert.True(live.Append(new(20, i, i, i, 0, i)));
        var prefix = live.Analyze(8500, estimator).History;
        Assert.True(prefix.ProjectionTruncated);
        Assert.Equal(0, prefix.Samples[0].Timestamp);
        Assert.Equal(8500, prefix.Samples[^1].Timestamp);
        var full = live.Analyze(8999, estimator).History;
        Assert.True(full.ProjectionTruncated);
        Assert.Equal(0, full.Samples[0].Timestamp);
        Assert.Equal(8999, full.Samples[^1].Timestamp);

        var expired = live.Analyze(38000, estimator).History;
        Assert.False(expired.ProjectionTruncated);
        Assert.Equal(1000, expired.Samples.Length);
        Assert.Equal(8000, expired.Samples[0].Timestamp);
        Assert.Equal(8999, expired.Samples[^1].Timestamp);
        Assert.Empty(live.Analyze(39000, estimator).History.Samples);

        Assert.True(live.Append(new(20, 9000, 40000, 0, .25, 8999)));
        var resumed = live.Analyze(40000, estimator).History;
        Assert.False(resumed.ProjectionTruncated);
        Assert.Equal(40000, Assert.Single(resumed.Samples).Timestamp);
    }

    [Fact]
    public void ProjectionBucketKeepsEveryRunAcrossExpansionExpiryAndReExpansion()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 15, capacity: 128, durationSeconds: 30);
        for (var run = 0; run < 5; run++)
        {
            var values = new[] { 0d, .4, .1, .3 };
            for (var point = 0; point < values.Length; point++)
            {
                var sequence = run * 4 + point;
                Assert.True(live.Append(new(15, sequence, 30000 + sequence, sequence, values[point], run)));
            }
        }

        var initial = live.Analyze(30019, new ModalStepEstimator()).History;
        Assert.False(initial.ProjectionTruncated);
        Assert.Equal(15, initial.Samples.Length);
        for (var run = 0; run < 5; run++)
        {
            Assert.Contains(initial.Samples, s => s.Sequence == run * 4);
            Assert.Contains(initial.Samples, s => s.Sequence == run * 4 + 1);
            Assert.Contains(initial.Samples, s => s.Sequence == run * 4 + 3);
        }

        var collapsed = live.Analyze(60016, new ModalStepEstimator()).History;
        Assert.Equal(3, collapsed.Samples.Length);
        Assert.Equal(16, collapsed.Samples[0].Sequence);

        for (var run = 5; run < 7; run++)
        {
            var values = new[] { 0d, .4, .1, .3 };
            for (var point = 0; point < values.Length; point++)
            {
                var sequence = run * 4 + point;
                Assert.True(live.Append(new(15, sequence, 30000 + sequence, sequence, values[point], run)));
            }
        }
        var expanded = live.Analyze(60016, new ModalStepEstimator()).History;
        Assert.False(expanded.ProjectionTruncated);
        Assert.Equal(9, expanded.Samples.Length);
        foreach (var sequence in new[] { 16, 17, 19, 20, 21, 23, 24, 25, 27 })
            Assert.Contains(expanded.Samples, s => s.Sequence == sequence);
    }

    [Fact]
    public void LongWindowPreservesVerySmallVarianceAcrossManyAggregateBlocks()
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.RX, clock, epoch: 6, capacity: 60000, durationSeconds: 60);
        var live = new LiveAxisWindow(Axis.RX, clock, epoch: 6, capacity: 60000, durationSeconds: 60);
        for (var i = 0; i < 50000; i++)
        {
            var value = .5 + ((i % 7) - 3) * 1e-10;
            var sample = new AxisSample(6, i, i, i, value, 0);
            Assert.True(reference.Append(sample)); Assert.True(live.Append(sample));
        }

        var expected = JitterCalculator.Calculate(reference.Snapshot(49999).Samples.Select(s => s.Value));
        var actual = live.Analyze(49999, new ModalStepEstimator()).Analysis.Jitter;
        Assert.Equal(50000, actual.Count);
        Assert.True(expected.PopulationStdDev > 0);
        Assert.True(actual.PopulationStdDev > 0);
        Assert.True(Math.Abs(actual.PopulationStdDev!.Value - expected.PopulationStdDev!.Value) / expected.PopulationStdDev.Value < 1e-5);
        Assert.True(Math.Abs(actual.Mean!.Value - expected.Mean!.Value) < 1e-12);
    }

    [Fact]
    public void MatureWindowAnalysisDoesNotAllocateAFullSampleCopy()
    {
        const int sampleCount = 240000;
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 7, capacity: 300000, durationSeconds: 300);
        for (var i = 0; i < sampleCount; i++)
            Assert.True(live.Append(new(7, i, i, i, (i % 256) / 255d - 1, 0)));
        var estimator = new ModalStepEstimator();
        var warm = live.Analyze(sampleCount - 1, estimator);
        var warmups = 0;
        while (warm.Analysis.Step.Message == "再計算中")
        {
            Assert.True(warmups++ < 10000, "Incremental session did not finish rebuilding");
            warm = live.Analyze(sampleCount - 1, estimator);
        }
        Assert.Equal(sampleCount, warm.Analysis.Count);
        Assert.NotEqual(EstimateStatus.NoData, warm.Analysis.Step.Status);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = live.Analyze(sampleCount - 1, estimator);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(sampleCount, result.Analysis.Count);
        Assert.True(allocated < 1024 * 1024, $"Mature analysis allocated {allocated:N0} bytes for one axis");
    }

    [Fact]
    public void EstimatorSwitchUsesBudgetAndKeepsWindowCountWhileRebuilding()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 12, capacity: 128, durationSeconds: 5);
        for (var i = 0; i < 80; i++)
            Assert.True(live.Append(new(12, i, i, i, i % 2 == 0 ? 0 : .1, 0)));
        var estimator = new ProbeEstimator("slow_probe", () => Thread.Sleep(8));

        var rebuilding = live.Analyze(79, estimator);
        Assert.Equal(80, rebuilding.Analysis.Count);
        Assert.Equal("slow_probe", rebuilding.Analysis.Step.MethodId);
        Assert.Equal(EstimateStatus.NoData, rebuilding.Analysis.Step.Status);
        Assert.Equal("再計算中", rebuilding.Analysis.Step.Message);

        var completed = live.Analyze(79, estimator);
        Assert.Equal(80, completed.Analysis.Count);
        Assert.Equal(79, completed.Analysis.Step.NonzeroCount);
        Assert.Equal(EstimateStatus.Reference, completed.Analysis.Step.Status);
    }

    [Fact]
    public void CancelledEstimatorRebuildResumesWithoutLosingIndexedEdges()
    {
        using var cancellation = new CancellationTokenSource();
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 13, capacity: 128, durationSeconds: 5);
        for (var i = 0; i < 24; i++)
            Assert.True(live.Append(new(13, i, i, i, i % 2 == 0 ? 0 : .1, 0)));
        var estimator = new ProbeEstimator("cancel_probe", cancellation.Cancel);

        Assert.Throws<OperationCanceledException>(() => live.Analyze(23, estimator, cancellation.Token));
        var resumed = live.Analyze(23, estimator);

        Assert.Equal(24, resumed.Analysis.Count);
        Assert.Equal(23, resumed.Analysis.Step.NonzeroCount);
        Assert.Equal(EstimateStatus.Reference, resumed.Analysis.Step.Status);
    }

    [Fact]
    public void CapacityExpiryAndFutureSamplesFollowReferenceSnapshotSemantics()
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.RX, clock, epoch: 2, capacity: 3, durationSeconds: 1);
        var live = new LiveAxisWindow(Axis.RX, clock, epoch: 2, capacity: 3, durationSeconds: 1);
        var estimator = new ModalStepEstimator();
        for (var i = 0; i < 4; i++)
        {
            var sample = new AxisSample(2, i, i * 100, i, i / 4d, 0);
            reference.Append(sample); live.Append(sample);
        }
        var result = live.Analyze(300, estimator);
        var snapshot = reference.Snapshot(300);
        Assert.Equal(snapshot.Samples.Length, result.Analysis.Count);
        Assert.Equal(snapshot.Truncated, result.Analysis.Truncated);
        Assert.Equal(snapshot.DroppedCount, result.Analysis.DroppedCount);
        AssertOptionalDouble(estimator.Estimate(snapshot.Differences.AsSpan()).Step, result.Analysis.Step.Step, 12);

        var future = new AxisSample(2, 4, 800, 4, .9, 0);
        Assert.True(reference.Append(future)); Assert.True(live.Append(future));
        var early = live.Analyze(300, estimator);
        var earlyReference = reference.Snapshot(300);
        Assert.Equal(earlyReference.Samples.Length, early.Analysis.Count);
        Assert.Equal(earlyReference.Samples[^1].Sequence, early.Analysis.Latest!.Value.Sequence);
        AssertOptionalDouble(estimator.Estimate(earlyReference.Differences.AsSpan()).Step, early.Analysis.Step.Step, 12);
    }

    [Fact]
    public void ThirtySecondStyleCutoffIsInclusiveAndExpiresWithoutNewInput()
    {
        var clock = new Clock();
        var live = new LiveAxisWindow(Axis.LY, clock, epoch: 1, durationSeconds: .01);
        live.Append(new(1, 0, 0, 0, -.2, 0));
        live.Append(new(1, 1, 1, 1, 0, 0));
        live.Append(new(1, 2, 10, 2, .2, 0));

        Assert.Equal(3, live.Analyze(10, new ModalStepEstimator()).Analysis.Count);
        var afterBoundary = live.Analyze(11, new ModalStepEstimator());
        Assert.Equal(2, afterBoundary.Analysis.Count);
        Assert.Equal(2, afterBoundary.Analysis.Latest!.Value.Sequence);
    }

    [Fact]
    public void TimeExpiryAdvancesWithoutInputAndResetClearsModalState()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 4, durationSeconds: .01);
        for (var i = 0; i < 20; i++) live.Append(new(4, i, i, i, i % 2 == 0 ? 0 : .1, 0));
        var before = live.Analyze(19, new ModalStepEstimator());
        Assert.True(before.Analysis.Count > 0);
        Assert.Equal(.1, before.Analysis.Step.Step!.Value, 12);
        var expired = live.Analyze(31, new ModalStepEstimator());
        Assert.Equal(0, expired.Analysis.Count);
        Assert.Equal(EstimateStatus.NoData, expired.Analysis.Step.Status);
        live.Reset(5);
        Assert.True(live.Append(new(5, 0, 31, 0, .5, 0)));
        var reset = live.Analyze(31, new ModalStepEstimator());
        Assert.Equal(5, reset.Analysis.Latest!.Value.Epoch);
        Assert.Equal(EstimateStatus.NoData, reset.Analysis.Step.Status);
    }

    [Fact]
    public void RejectedInputCutsTheFollowingEdgeWithoutCountingOtherEpochs()
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.LY, clock, epoch: 9);
        var live = new LiveAxisWindow(Axis.LY, clock, epoch: 9);
        var samples = new[]
        {
            new AxisSample(9, 0, 10, 0, .1, 0),
            new AxisSample(8, 1, 11, 1, .2, 0), // Wrong epoch is ignored.
            new AxisSample(9, 1, 11, 1, double.NaN, 0), // Rejected value cuts the next edge.
            new AxisSample(9, 2, 12, 2, .4, -1), // Local continuity increment cancels this source ID change.
            new AxisSample(9, 3, 13, 3, .5, -1)
        };
        foreach (var sample in samples)
        {
            var acceptedByReference = reference.Append(sample);
            var acceptedByLive = live.Append(sample);
            Assert.Equal(acceptedByReference, acceptedByLive);
            if (sample.Epoch == 9 && double.IsFinite(sample.Value)) Assert.True(acceptedByLive);
            else Assert.False(acceptedByLive);
        }
        var expected = reference.Snapshot(13);
        var actual = live.Analyze(13, new ModalStepEstimator());
        Assert.Equal(expected.Samples.Length, actual.Analysis.Count);
        Assert.Equal(expected.DroppedCount, actual.Analysis.DroppedCount);
        Assert.Equal(1, actual.Analysis.Step.NonzeroCount);
        Assert.Equal(.1, actual.Analysis.Step.Step!.Value, 12);
    }

    [Fact]
    public void ModalTiesChooseTheSmallestBucketAndExpireWithTheirEndpoint()
    {
        var live = new LiveAxisWindow(Axis.LX, new Clock(), epoch: 1, durationSeconds: .05);
        long sequence = 0;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(live.Append(new(1, sequence, sequence, sequence, i % 2 == 0 ? 0 : .2, 0))); sequence++;
        }
        for (var i = 0; i < 20; i++)
        {
            Assert.True(live.Append(new(1, sequence, sequence, sequence, i % 2 == 0 ? 0 : .1, 1))); sequence++;
        }
        Assert.Equal(40, live.Analyze(39, new ModalStepEstimator()).Analysis.Count);
        var tie = live.Analyze(39, new ModalStepEstimator());
        Assert.True(tie.Analysis.Step.Tie);
        Assert.Equal(.1, tie.Analysis.Step.Step!.Value, 12);

        var expired = live.Analyze(70, new ModalStepEstimator());
        Assert.Equal(20, expired.Analysis.Count);
        Assert.False(expired.Analysis.Step.Tie);
        Assert.Equal(.1, expired.Analysis.Step.Step!.Value, 12);
        var empty = live.Analyze(90, new ModalStepEstimator());
        Assert.Equal(0, empty.Analysis.Count);
        Assert.Equal(EstimateStatus.NoData, empty.Analysis.Step.Status);
    }

    [Theory]
    [InlineData(8.049999999)]
    [InlineData(8.05)]
    [InlineData(8.050000001)]
    public void RollingBitFormattingMatchesLegacySumAtRoundingBoundaries(double bits)
    {
        var clock = new Clock();
        var reference = new AxisWindow(Axis.LX, clock, capacity: 512, durationSeconds: 2);
        var live = new LiveAxisWindow(Axis.LX, clock, capacity: 512, durationSeconds: 2);
        var estimator = new ModalStepEstimator();
        var step = 2 / Math.Pow(2, bits);
        for (var i = 0; i < 8000; i++)
        {
            var value = .3 + (i % 2 == 0 ? 0 : step) + ((i % 5) - 2) * 2e-14;
            var sample = new AxisSample(0, i, i, i, value, 0);
            Assert.True(reference.Append(sample)); Assert.True(live.Append(sample));
            if (i < 33 || i % 17 != 0) continue;
            var result = live.Analyze(i, estimator);
            for (var retry = 0; result.Analysis.Step.Message == "再計算中" && retry < 1000; retry++) result = live.Analyze(i, estimator);
            var sorted = reference.Snapshot(i).Differences.ToArray(); Array.Sort(sorted);
            // The pre-optimization modal estimator sorted its winning bucket and used this compensated sum.
            double sum = 0, correction = 0;
            foreach (var delta in sorted)
            {
                var next = sum + delta;
                correction += Math.Abs(sum) >= Math.Abs(delta) ? (sum-next)+delta : (delta-next)+sum;
                sum = next;
            }
            var expectedStep = (sum + correction) / sorted.Length;
            Assert.Equal(StepPrecision.FormatBits(Math.Log2(2 / expectedStep)), StepPrecision.FormatBits(result.Analysis.Step.Bits));
        }
    }

    private static void AssertOptionalDouble(double? expected, double? actual, int precision)
    {
        if (expected.HasValue) Assert.Equal(expected.Value, actual!.Value, precision);
        else Assert.Null(actual);
    }
}
