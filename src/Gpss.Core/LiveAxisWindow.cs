using System.Collections.Immutable;
using System.Diagnostics;

namespace Gpss.Core;

/// <summary>The exact current analysis and a bounded, display-only history projection for one axis.</summary>
public sealed record LiveAxisResult(AxisAnalysis Analysis, AxisWindowSnapshot History,
    ImmutableArray<StepHistogramBin> StepHistogram = default);

/// <summary>
/// Bounded single-writer window for live analysis. Statistics are combined from 256-sample blocks;
/// modal deltas are indexed incrementally and history is projected from fixed absolute-time buckets.
/// </summary>
public sealed class LiveAxisWindow
{
    private const int BlockSize = 256;
    private const int MaximumProjectionPoints = 8192;
    private const int ProjectionBuckets = 1024;
    private readonly Axis _axis;
    private readonly long _frequency, _duration, _projectionBucketTicks;
    private readonly int _capacity, _blockLeafCount;
    private readonly PagedArray<AxisSample> _samples;
    private readonly PagedArray<long> _projectionContinuities;
    private readonly PagedArray<long> _ordinals;
    private readonly PagedArray<Edge> _edges;
    private readonly int[] _pageSampleCounts;
    /// <summary>Raw sample slots currently backed by pages; excludes fixed analysis/projection metadata.</summary>
    public int AllocatedSampleCapacity => _samples.AllocatedCapacity;
    private readonly Metric[] _blockMetrics, _segmentTree;
    private readonly long[] _blockIds, _treeBlockIds;
    private readonly SortedDictionary<long, ProjectionBucket> _projection = new();
    private double[] _scratch = [];
    private readonly AxisSample[] _projectionOutput = new AxisSample[MaximumProjectionPoints];
    private readonly List<(long Bucket, long Segment)> _dirtyProjectionKeys = new();
    private readonly StepHistogramCounts _stepHistogram = new();
    private int _projectionCount;
    private bool _projectionOverflow;
    private long _epoch, _startOrdinal, _nextOrdinal;
    private long? _lastSequence, _lastTimestamp, _lastTruncatedTimestamp;
    private long _dropped, _localContinuity, _projectionRun;
    private long? _lastSourceContinuity;
    private bool _breakNext;
    private IStepEstimatorSession? _stepSession;
    private IIncrementalStepEstimator? _sessionEstimator;
    private IStepEstimatorSession? _pendingStepSession;
    private IIncrementalStepEstimator? _pendingSessionEstimator;
    private long _pendingNextOrdinal;

    public LiveAxisWindow(Axis axis, IMonotonicClock clock, long epoch = 0, int capacity = 300000, double durationSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (clock.Frequency <= 0 || capacity <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            durationSeconds * clock.Frequency > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Window limits must be positive and representable");
        _axis = axis; _frequency = clock.Frequency;
        _duration = checked((long)Math.Ceiling(durationSeconds * clock.Frequency));
        _capacity = capacity;
        _projectionBucketTicks = Math.Max(1, checked((long)Math.Ceiling(_duration / (double)(ProjectionBuckets - 1))));
        _samples = new(capacity); _projectionContinuities = new(capacity);
        _ordinals = new(capacity, long.MinValue); _edges = new(capacity);
        _pageSampleCounts = new int[_samples.PageCount];
        _blockLeafCount = 1;
        while (_blockLeafCount < checked((int)Math.Ceiling(capacity / (double)BlockSize) + 2)) _blockLeafCount <<= 1;
        _blockMetrics = new Metric[_blockLeafCount]; _blockIds = new long[_blockLeafCount]; _treeBlockIds = new long[_blockLeafCount];
        Array.Fill(_blockIds, long.MinValue);
        Array.Fill(_treeBlockIds, long.MinValue);
        _segmentTree = new Metric[2 * _blockLeafCount];
        Reset(epoch);
    }

    public void Reset(long epoch)
    {
        _epoch = epoch; _startOrdinal = _nextOrdinal = 0;
        _samples.Clear(); _projectionContinuities.Clear(); _ordinals.Clear(); _edges.Clear();
        Array.Clear(_pageSampleCounts);
        Array.Clear(_blockMetrics, 0, _blockMetrics.Length);
        Array.Fill(_blockIds, long.MinValue); Array.Fill(_treeBlockIds, long.MinValue);
        Array.Clear(_segmentTree, 0, _segmentTree.Length); _projection.Clear();
        _lastSequence = _lastTimestamp = _lastTruncatedTimestamp = null;
        _dropped = _localContinuity = _projectionRun = 0; _lastSourceContinuity = null;
        _breakNext = false; _count = 0; _stepSession = null; _sessionEstimator = null;
        _pendingStepSession = null; _pendingSessionEstimator = null; _pendingNextOrdinal = 0;
        _projectionCount = 0;
        _projectionOverflow = false; _dirtyProjectionKeys.Clear();
        _stepHistogram.Clear();
    }

    public bool Append(AxisSample sample)
    {
        if (sample.Epoch != _epoch) return false;
        if (sample.Sequence < 0 || sample.Timestamp < 0 ||
            (_lastSequence.HasValue && sample.Sequence <= _lastSequence.Value) ||
            (_lastTimestamp.HasValue && sample.Timestamp < _lastTimestamp.Value))
        {
            if (!_lastSequence.HasValue || sample.Sequence > _lastSequence.Value) _lastSequence = sample.Sequence;
            _dropped++; _breakNext = true; return false;
        }
        _lastSequence = sample.Sequence; _lastTimestamp = sample.Timestamp;
        if (!double.IsFinite(sample.Value) || sample.Value < -1 || sample.Value > 1)
        { _dropped++; _breakNext = true; return false; }

        Expire(sample.Timestamp);
        var explicitBreak = _breakNext;
        if (explicitBreak) _localContinuity++;
        if (explicitBreak || (_lastSourceContinuity is long previousContinuity && previousContinuity != sample.ContinuityId))
            _projectionRun++;
        _lastSourceContinuity = sample.ContinuityId;
        _breakNext = false;
        var stored = sample with { ContinuityId = unchecked(sample.ContinuityId + _localContinuity) };
        if (_count == _capacity)
        {
            var previousStart = _startOrdinal;
            var lost = Get(_startOrdinal); // Remove the old ring occupant before its slot can be reused.
            RemoveHead();
            _lastTruncatedTimestamp = lost.Timestamp; _dropped++;
            RefreshBlocksAfterRemoval(previousStart, _startOrdinal);
        }
        var ordinal = _nextOrdinal++;
        var slot = Slot(ordinal);
        _samples[slot] = stored; _projectionContinuities[slot] = _projectionRun; _ordinals[slot] = ordinal;
        _pageSampleCounts[PagedArray<AxisSample>.GetPageIndex(slot)]++;
        if (_count > 0)
        {
            var previous = Get(_nextOrdinal - 2);
            if (!explicitBreak && previous.ContinuityId == stored.ContinuityId)
            {
                var edge = new Edge(true, Math.Abs(stored.Value - previous.Value));
                _edges[slot] = edge; _stepSession?.Add(edge.Delta);
                _stepHistogram.AddDifference(edge.Delta);
            }
        }
        AddToBlock(ordinal, stored);
        AddToProjection(stored with { ContinuityId = _projectionRun });
        _count++;
        if (_count == 1) _startOrdinal = ordinal;
        return true;
    }

    private int _count;

    public LiveAxisResult Analyze(long now, IStepEstimator estimator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        Expire(now, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var visibleEnd = UpperBound(now, _nextOrdinal);
        var visibleCount = checked((int)(visibleEnd - _startOrdinal));
        var stats = AggregateSamples(_startOrdinal, visibleEnd, cancellationToken);
        var first = visibleCount == 0 ? (AxisSample?)null : Get(_startOrdinal);
        var latest = visibleCount == 0 ? (AxisSample?)null : Get(visibleEnd - 1);
        var retained = visibleCount > 1 ? (latest!.Value.Timestamp - first!.Value.Timestamp) / (double)_frequency : 0;

        StepEstimate step;
        if (estimator is IIncrementalStepEstimator incrementalEstimator)
        {
            var sessionReady = EnsureStepSession(incrementalEstimator, cancellationToken);
            if (!sessionReady) step = new(estimator.MethodId, Message: "再計算中");
            else if (visibleEnd == _nextOrdinal) step = _stepSession!.Estimate();
            else
            {
                var differenceCount = FillDifferences(visibleEnd, cancellationToken);
                step = estimator.Estimate(_scratch.AsSpan(0, differenceCount));
            }
        }
        else
        {
            var differenceCount = FillDifferences(visibleEnd, cancellationToken);
            step = estimator.Estimate(_scratch.AsSpan(0, differenceCount));
        }
        var jitter = stats.Count == 0 ? new AxisJitter(0, null, null, null) :
            new AxisJitter(stats.Count, stats.Mean, Math.Sqrt(Math.Max(0, stats.M2 / stats.Count)),
                Math.Max(Math.Abs(stats.Min - stats.Mean), Math.Abs(stats.Max - stats.Mean)));
        var recentStart = now < long.MinValue + _frequency ? long.MinValue : now - _frequency;
        var recentCount = CountInRange(recentStart, now, visibleEnd, out var recentFirst, out var recentLast);
        var sampleRate = recentCount > 1 && recentLast > recentFirst
            ? (recentCount - 1) * (double)_frequency / (recentLast - recentFirst) : 0;
        var analysis = new AxisAnalysis(_axis, latest, jitter, step, stats.Count, retained,
            IsAcquisitionTruncated(now), _dropped, sampleRate);
        var history = BuildHistory(now, visibleEnd, visibleCount, cancellationToken);
        return new(analysis, history, SnapshotStepHistogram(visibleEnd, cancellationToken));
    }

    private ImmutableArray<StepHistogramBin> SnapshotStepHistogram(long visibleEnd, CancellationToken cancellationToken)
    {
        if (visibleEnd == _nextOrdinal) return _stepHistogram.Snapshot();
        // The rare caller-supplied past clock must not count edges from future samples.
        var counts = new StepHistogramCounts();
        for (var ordinal = _startOrdinal + 1; ordinal < visibleEnd; ordinal++)
        {
            if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var edge = _edges[Slot(ordinal)];
            if (edge.Exists) counts.AddDifference(edge.Delta);
        }
        return counts.Snapshot();
    }

    private long Cutoff(long now) => now < long.MinValue + _duration ? long.MinValue : now - _duration;

    private void Expire(long now, CancellationToken cancellationToken = default)
    {
        var oldStart = _startOrdinal;
        var cutoff = Cutoff(now);
        try
        {
            while (_count > 0 && Get(_startOrdinal).Timestamp < cutoff)
            {
                if ((_startOrdinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                RemoveHead();
            }
        }
        finally
        {
            if (_startOrdinal != oldStart) RefreshBlocksAfterRemoval(oldStart, _startOrdinal);
        }
    }

    private void RemoveHead()
    {
        if (_count == 0) return;
        var oldStart = _startOrdinal;
        RemoveEdgeAt(oldStart);
        var projected = Projected(oldStart);
        _projection.TryGetValue(ProjectionBucketId(projected.Timestamp), out var bucket);
        if (bucket is not null)
        {
            var bucketId = ProjectionBucketId(projected.Timestamp);
            if (bucket.TryGetSegment(projected.ContinuityId, out var segmentNode))
            {
                var segment = segmentNode.Segment;
                segment.Count--; bucket.Count--;
                bucket.SummaryDirty = true;
                if (segment.Count <= 0) bucket.RemoveSegment(projected.ContinuityId);
                else if (!segment.Dirty)
                {
                    segment.Dirty = true;
                    _dirtyProjectionKeys.Add((ProjectionBucketId(projected.Timestamp), projected.ContinuityId));
                }
            }
            if (bucket.Count <= 0) _projection.Remove(bucketId);
        }
        _ordinals[Slot(oldStart)] = long.MinValue;
        _startOrdinal++; _count--;
        if (_count > 0) RemoveEdgeAt(_startOrdinal); // Its preceding endpoint has left the window.
        else _startOrdinal = _nextOrdinal;
        var pageIndex = PagedArray<AxisSample>.GetPageIndex(Slot(oldStart));
        if (--_pageSampleCounts[pageIndex] == 0)
        {
            // Ordinal ring slots move even at low rates. Release empty pages so a
            // sparse long-running session does not eventually fill the entire maximum ring.
            _samples.ReleasePage(pageIndex); _projectionContinuities.ReleasePage(pageIndex);
            _ordinals.ReleasePage(pageIndex); _edges.ReleasePage(pageIndex);
        }
    }

    private void RemoveEdgeAt(long ordinal)
    {
        var slot = Slot(ordinal);
        var edge = _edges[slot];
        if (edge.Exists)
        {
            _stepHistogram.RemoveDifference(edge.Delta);
            _stepSession?.Remove(edge.Delta);
            if (_pendingStepSession is not null && ordinal < _pendingNextOrdinal)
                _pendingStepSession.Remove(edge.Delta);
        }
        _edges[slot] = default;
    }

    private int Slot(long ordinal) => (int)(ordinal % _capacity);
    private AxisSample Projected(long ordinal)
    {
        var slot = Slot(ordinal);
        return Get(ordinal) with { ContinuityId = _projectionContinuities[slot] };
    }
    private AxisSample Get(long ordinal)
    {
        var slot = Slot(ordinal);
        if (_ordinals[slot] != ordinal) throw new InvalidOperationException("Live window ring slot is stale");
        return _samples[slot];
    }

    private void AddToBlock(long ordinal, AxisSample sample)
    {
        var blockId = ordinal / BlockSize;
        var index = (int)(blockId % _blockLeafCount);
        if (_blockIds[index] != blockId)
        {
            if (_treeBlockIds[index] != long.MinValue) SetTreeBlock(_treeBlockIds[index], Metric.Empty);
            _blockIds[index] = blockId; _blockMetrics[index] = Metric.Empty;
        }
        _blockMetrics[index] = Metric.Merge(_blockMetrics[index], Metric.Single(sample.Value));
        if (ordinal % BlockSize == BlockSize - 1 && _startOrdinal <= blockId * BlockSize)
            SetTreeBlock(blockId, _blockMetrics[index]);
    }

    private void RefreshBlocksAfterRemoval(long previousStart, long newStart)
    {
        var oldFirstBlock = previousStart / BlockSize;
        var lastPossibleBlock = (_nextOrdinal - 1) / BlockSize;
        if (_count == 0)
        {
            for (var id = oldFirstBlock; id <= lastPossibleBlock; id++)
            {
                var index = (int)(id % _blockLeafCount);
                if (_treeBlockIds[index] == id) SetTreeBlock(id, Metric.Empty);
                if (_blockIds[index] == id) { _blockIds[index] = long.MinValue; _blockMetrics[index] = Metric.Empty; }
            }
            return;
        }
        var firstActiveBlock = newStart / BlockSize;
        for (var id = oldFirstBlock; id < firstActiveBlock; id++)
        {
            var index = (int)(id % _blockLeafCount);
            if (_treeBlockIds[index] == id) SetTreeBlock(id, Metric.Empty);
            if (_blockIds[index] == id) { _blockIds[index] = long.MinValue; _blockMetrics[index] = Metric.Empty; }
        }
        if (newStart % BlockSize != 0)
        {
            var index = (int)(firstActiveBlock % _blockLeafCount);
            if (_treeBlockIds[index] == firstActiveBlock) SetTreeBlock(firstActiveBlock, Metric.Empty);
        }
    }

    private void SetTreeBlock(long blockId, Metric value)
    {
        var leaf = (int)(blockId % _blockLeafCount);
        _treeBlockIds[leaf] = value.Count == 0 ? long.MinValue : blockId;
        var node = _blockLeafCount + leaf; _segmentTree[node] = value;
        while ((node >>= 1) > 0) _segmentTree[node] = Metric.Merge(_segmentTree[node * 2], _segmentTree[node * 2 + 1]);
    }

    private Metric AggregateSamples(long start, long end, CancellationToken cancellationToken)
    {
        if (start >= end) return Metric.Empty;
        var firstBlock = start / BlockSize; var lastBlock = (end - 1) / BlockSize;
        if (firstBlock == lastBlock) return Scan(start, end, cancellationToken);
        var result = Metric.Empty;
        var fullStart = start;
        if (start % BlockSize != 0)
        {
            var boundary = Math.Min(end, (firstBlock + 1) * BlockSize);
            result = Metric.Merge(result, Scan(start, boundary, cancellationToken)); fullStart = boundary;
        }
        var fullEnd = end - end % BlockSize;
        if (fullStart < fullEnd)
            result = Metric.Merge(result, QueryBlocks(fullStart / BlockSize, fullEnd / BlockSize - 1));
        if (fullEnd < end) result = Metric.Merge(result, Scan(fullEnd, end, cancellationToken));
        return result;
    }

    private Metric Scan(long start, long end, CancellationToken cancellationToken)
    {
        var result = Metric.Empty;
        for (var i = start; i < end; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            result = Metric.Merge(result, Metric.Single(Get(i).Value));
        }
        return result;
    }

    private Metric QueryBlocks(long firstId, long lastId)
    {
        if (firstId > lastId) return Metric.Empty;
        var length = checked((int)(lastId - firstId + 1));
        var start = (int)(firstId % _blockLeafCount);
        var firstLength = Math.Min(length, _blockLeafCount - start);
        var value = QueryTree(start, start + firstLength);
        if (firstLength < length) value = Metric.Merge(value, QueryTree(0, length - firstLength));
        return value;
    }

    private Metric QueryTree(int left, int right)
    {
        var l = left + _blockLeafCount; var r = right + _blockLeafCount;
        var leftValue = Metric.Empty; var rightValue = Metric.Empty;
        while (l < r)
        {
            if ((l & 1) != 0) leftValue = Metric.Merge(leftValue, _segmentTree[l++]);
            if ((r & 1) != 0) rightValue = Metric.Merge(_segmentTree[--r], rightValue);
            l >>= 1; r >>= 1;
        }
        return Metric.Merge(leftValue, rightValue);
    }

    private int CountInRange(long from, long through, long visibleEnd, out long firstTime, out long lastTime)
    {
        firstTime = lastTime = 0; var count = 0;
        var low = LowerBound(from, visibleEnd); var high = UpperBound(through, visibleEnd);
        if (low >= high) return 0;
        count = checked((int)(high - low)); firstTime = Get(low).Timestamp; lastTime = Get(high - 1).Timestamp;
        return count;
    }

    private long LowerBound(long timestamp, long end)
    {
        var lo = _startOrdinal; var hi = end;
        while (lo < hi) { var mid = lo + ((hi - lo) >> 1); if (Get(mid).Timestamp < timestamp) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private long UpperBound(long timestamp, long end)
    {
        var lo = _startOrdinal; var hi = end;
        while (lo < hi) { var mid = lo + ((hi - lo) >> 1); if (Get(mid).Timestamp <= timestamp) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private int FillDifferences(long visibleEnd, CancellationToken cancellationToken)
    {
        var required = Math.Max(0, _count - 1);
        if (_scratch.Length < required) _scratch = new double[Math.Min(_capacity, Math.Max(required, _scratch.Length * 2))];
        var count = 0;
        for (var ordinal = _startOrdinal + 1; ordinal < visibleEnd; ordinal++)
        {
            if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (_edges[Slot(ordinal)].Exists) _scratch[count++] = _edges[Slot(ordinal)].Delta;
        }
        return count;
    }

    private bool IsAcquisitionTruncated(long now) => _lastTruncatedTimestamp is long lost && lost >= Cutoff(now);

    private bool EnsureStepSession(IIncrementalStepEstimator estimator, CancellationToken cancellationToken)
    {
        if (_stepSession is not null && (ReferenceEquals(_sessionEstimator, estimator) ||
            (_sessionEstimator is ModalStepEstimator activeModal && estimator is ModalStepEstimator requestedModal &&
             activeModal.Config == requestedModal.Config)))
        {
            _sessionEstimator = estimator;
            _pendingStepSession = null; _pendingSessionEstimator = null; _pendingNextOrdinal = 0;
            return true;
        }
        if (_pendingStepSession is null || !ReferenceEquals(_pendingSessionEstimator, estimator))
        {
            var created = estimator.CreateSession();
            if (created.MethodId != estimator.MethodId)
                throw new InvalidOperationException("Incremental estimator session ID does not match its estimator");
            _pendingStepSession = created;
            _pendingSessionEstimator = estimator;
            _pendingNextOrdinal = _startOrdinal + 1;
        }

        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        while (_pendingNextOrdinal < _nextOrdinal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Expiration may have moved the head beyond work that was pending.
            if (_pendingNextOrdinal <= _startOrdinal) _pendingNextOrdinal = _startOrdinal + 1;
            if (_pendingNextOrdinal >= _nextOrdinal) break;
            var ordinal = _pendingNextOrdinal;
            var edge = _edges[Slot(ordinal)];
            if (edge.Exists) _pendingStepSession!.Add(edge.Delta);
            _pendingNextOrdinal = ordinal + 1;
            processed++;
            if (_pendingNextOrdinal < _nextOrdinal && processed > 0 && Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(2))
                return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Publish only a complete session; cancellation leaves pending work resumable.
        _stepSession = _pendingStepSession; _sessionEstimator = estimator;
        _pendingStepSession = null; _pendingSessionEstimator = null; _pendingNextOrdinal = 0;
        return true;
    }

    private long ProjectionBucketId(long timestamp) => timestamp / _projectionBucketTicks;
    private void AddToProjection(AxisSample sample)
    {
        var id = ProjectionBucketId(sample.Timestamp);
        if (!_projection.TryGetValue(id, out var bucket)) _projection.Add(id, bucket = new());
        if (!bucket.TryGetSegment(sample.ContinuityId, out var segmentNode))
            segmentNode = bucket.AddSegment(sample.ContinuityId, new());
        var segment = segmentNode.Segment;
        bucket.Count++; bucket.Summary.Add(sample); segment.Add(sample);
    }

    private void RefreshDirtyProjectionBuckets(CancellationToken cancellationToken)
    {
        while (_dirtyProjectionKeys.Count > 0)
        {
            var keyIndex = _dirtyProjectionKeys.Count - 1;
            var key = _dirtyProjectionKeys[keyIndex];
            cancellationToken.ThrowIfCancellationRequested();
            if (!_projection.TryGetValue(key.Bucket, out var projectionBucket) ||
                !projectionBucket.TryGetSegment(key.Segment, out _))
            { _dirtyProjectionKeys.RemoveAt(keyIndex); continue; }
            var startTime = key.Bucket * _projectionBucketTicks;
            var endTime = startTime > long.MaxValue - _projectionBucketTicks ? long.MaxValue : startTime + _projectionBucketTicks;
            var first = LowerBound(startTime, _nextOrdinal);
            var end = LowerBound(endTime, _nextOrdinal);
            var rebuilt = new ProjectionSegment();
            for (var ordinal = first; ordinal < end; ordinal++)
            {
                if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sample = Projected(ordinal);
                if (sample.ContinuityId == key.Segment) rebuilt.Add(sample);
            }
            if (rebuilt.Count == 0) projectionBucket.RemoveSegment(key.Segment);
            else projectionBucket.ReplaceSegment(key.Segment, rebuilt);
            _dirtyProjectionKeys.RemoveAt(keyIndex);
        }
        foreach (var bucket in _projection.Values)
        {
            if (!bucket.SummaryDirty) continue;
            var summary = new ProjectionSummary(); var visited = 0;
            foreach (var node in bucket)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                summary.Include(node.Segment);
            }
            bucket.Summary = summary;
            bucket.SummaryDirty = false;
        }
    }

    private AxisWindowSnapshot BuildHistory(long now, long visibleEnd, int visibleCount, CancellationToken cancellationToken)
    {
        if (visibleCount == 0)
            return new(_epoch, _axis, now, _frequency, [], [], 0, _dropped, IsAcquisitionTruncated(now), false,
                _duration / (double)_frequency);
        RefreshDirtyProjectionBuckets(cancellationToken);
        var buckets = _projection;
        if (visibleEnd != _nextOrdinal)
        {
            // Snapshot semantics permit a caller-supplied clock before already appended samples.
            // This uncommon path scans only the visible prefix and does not contaminate its projection.
            var fallback = new SortedDictionary<long, ProjectionBucket>();
            for (var ordinal = _startOrdinal; ordinal < visibleEnd; ordinal++)
            {
                if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sample = Projected(ordinal); var id = ProjectionBucketId(sample.Timestamp);
                if (!fallback.TryGetValue(id, out var bucket)) fallback.Add(id, bucket = new());
                if (!bucket.TryGetSegment(sample.ContinuityId, out var segmentNode))
                    segmentNode = bucket.AddSegment(sample.ContinuityId, new());
                segmentNode.Segment.Add(sample); bucket.Summary.Add(sample);
            }
            buckets = fallback;
        }
        _projectionCount = 0; _projectionOverflow = false;
        var segmentCount = 0;
        foreach (var bucket in buckets.Values)
        {
            segmentCount += bucket.SegmentCount;
            if (segmentCount > MaximumProjectionPoints) { _projectionOverflow = true; break; }
        }
        var visited = 0;
        if (!_projectionOverflow) foreach (var bucket in buckets.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var segment in bucket)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                AddSegmentCandidates(segment.Segment, now);
                if (_projectionOverflow) break;
            }
            if (_projectionOverflow) break;
        }
        if (_projectionOverflow)
        {
            // Too many continuity runs: coarsen every time bucket, not the time range.
            // At most 1024 buckets * four extrema fit within the fixed output buffer.
            _projectionCount = 0; _projectionOverflow = false;
            foreach (var bucket in buckets.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = bucket.Summary;
                AddCandidates(summary.First, summary.Min, summary.Max, summary.Last, now);
            }
            _projectionOverflow = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var builder = ImmutableArray.CreateBuilder<AxisSample>(_projectionCount);
        for (var i = 0; i < _projectionCount; i++) builder.Add(_projectionOutput[i]);
        var projected = builder.MoveToImmutable();
        var retained = projected.Length > 1 ? (projected[^1].Timestamp - projected[0].Timestamp) / (double)_frequency : 0;
        var truncated = IsAcquisitionTruncated(now);
        return new(_epoch, _axis, now, _frequency, projected, [], retained, _dropped,
            truncated, _projectionOverflow, _duration / (double)_frequency);
    }

    private void AddSegmentCandidates(ProjectionSegment segment, long now)
        => AddCandidates(segment.First, segment.Min, segment.Max, segment.Last, now);

    private void AddCandidates(AxisSample first, AxisSample min, AxisSample max, AxisSample last, long now)
    {
        Span<AxisSample> candidates = stackalloc AxisSample[4];
        Span<AxisSample> selected = stackalloc AxisSample[4];
        candidates[0] = first; candidates[1] = min; candidates[2] = max; candidates[3] = last;
        for (var i = 1; i < candidates.Length; i++)
        {
            var value = candidates[i]; var j = i - 1;
            while (j >= 0 && candidates[j].Sequence > value.Sequence) { candidates[j + 1] = candidates[j]; j--; }
            candidates[j + 1] = value;
        }
        long previousSequence = long.MinValue;
        var selectedCount = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Sequence == previousSequence) continue;
            previousSequence = candidate.Sequence;
            if (candidate.Timestamp <= now) selected[selectedCount++] = candidate;
        }
        PushProjectionSegment(selected[..selectedCount]);
    }

    private void PushProjectionSegment(ReadOnlySpan<AxisSample> samples)
    {
        if (samples.IsEmpty || _projectionOverflow) return;
        if (_projectionCount + samples.Length > MaximumProjectionPoints)
        {
            _projectionOverflow = true;
            return;
        }
        samples.CopyTo(_projectionOutput.AsSpan(_projectionCount));
        _projectionCount += samples.Length;
    }

    private struct Metric(int count, double mean, double m2, double min, double max)
    {
        public int Count = count;
        public double Mean = mean, M2 = m2, Min = min, Max = max;
        public static Metric Empty => default;
        public static Metric Single(double value) => new(1, value, 0, value, value);
        public static Metric Merge(Metric a, Metric b)
        {
            if (a.Count == 0) return b;
            if (b.Count == 0) return a;
            var total = a.Count + b.Count; var delta = b.Mean - a.Mean;
            var mean = a.Mean + delta * (b.Count / (double)total);
            var m2 = a.M2 + b.M2 + delta * delta * (a.Count * (double)b.Count / total);
            return new(total, mean, m2, Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
        }
    }

    private readonly record struct Edge(bool Exists, double Delta);

    private sealed class ProjectionBucket
    {
        private ProjectionSegmentNode? _single;
        private Dictionary<long, ProjectionSegmentNode>? _segments;
        private LinkedList<ProjectionSegmentNode>? _ordered;
        public int Count;
        public int SegmentCount => _segments?.Count ?? (_single is null ? 0 : 1);
        public ProjectionSummary Summary;
        public bool SummaryDirty;
        public ProjectionBucketEnumerator GetEnumerator() => new(_single, _ordered);
        public bool TryGetSegment(long key, out ProjectionSegmentNode node)
        {
            if (_segments is not null) return _segments.TryGetValue(key, out node!);
            if (_single is not null && _single.Key == key) { node = _single; return true; }
            node = null!; return false;
        }
        public ProjectionSegmentNode AddSegment(long key, ProjectionSegment segment)
        {
            var node = new ProjectionSegmentNode(key, segment);
            if (_segments is not null)
            {
                AddOrdered(node);
                return node;
            }
            if (_single is null) { _single = node; return node; }
            _segments = new Dictionary<long, ProjectionSegmentNode>();
            _ordered = new LinkedList<ProjectionSegmentNode>();
            AddOrdered(_single!);
            _single = null;
            AddOrdered(node);
            return node;
        }
        private void AddOrdered(ProjectionSegmentNode node)
        {
            node.OrderNode = _ordered!.AddLast(node);
            _segments!.Add(node.Key, node);
        }
        public void ReplaceSegment(long key, ProjectionSegment segment)
        {
            if (TryGetSegment(key, out var node)) node.Segment = segment;
        }
        public void RemoveSegment(long key)
        {
            if (_segments is null)
            {
                if (_single?.Key == key) _single = null;
                return;
            }
            if (!_segments.Remove(key, out var node)) return;
            _ordered!.Remove(node.OrderNode!);
            if (_segments.Count == 1)
            {
                foreach (var remaining in _segments.Values) { _single = remaining; break; }
                _segments = null; _ordered = null;
                _single!.OrderNode = null;
            }
            else if (_segments.Count == 0)
            { _segments = null; _ordered = null; _single = null; }
        }
    }

    private struct ProjectionBucketEnumerator
    {
        private readonly ProjectionSegmentNode? _single;
        private readonly bool _useList;
        private bool _singleVisited;
        private LinkedList<ProjectionSegmentNode>.Enumerator _listEnumerator;
        public ProjectionSegmentNode Current { get; private set; } = null!;
        public ProjectionBucketEnumerator(ProjectionSegmentNode? single, LinkedList<ProjectionSegmentNode>? ordered)
        {
            _single = single; _useList = ordered is not null; _singleVisited = false;
            _listEnumerator = ordered is null ? default : ordered.GetEnumerator();
            Current = null!;
        }
        public bool MoveNext()
        {
            if (_useList)
            {
                if (!_listEnumerator.MoveNext()) return false;
                Current = _listEnumerator.Current; return true;
            }
            if (_singleVisited || _single is null) return false;
            _singleVisited = true; Current = _single; return true;
        }
    }

    private sealed class ProjectionSegmentNode(long key, ProjectionSegment segment)
    {
        public long Key { get; } = key;
        public ProjectionSegment Segment { get; set; } = segment;
        public LinkedListNode<ProjectionSegmentNode>? OrderNode { get; set; }
    }

    private sealed class ProjectionSegment
    {
        public int Count;
        public bool Dirty;
        public AxisSample First, Last, Min, Max;
        public void Add(AxisSample sample)
        {
            if (Count == 0) First = Min = Max = sample;
            else
            {
                if (sample.Value < Min.Value) Min = sample;
                if (sample.Value > Max.Value) Max = sample;
            }
            Last = sample; Count++;
        }
    }

    private struct ProjectionSummary
    {
        private bool _hasSamples;
        public AxisSample First, Last, Min, Max;
        public void Add(AxisSample sample)
        {
            if (!_hasSamples) { First = Min = Max = sample; _hasSamples = true; }
            else
            {
                if (sample.Value < Min.Value) Min = sample;
                if (sample.Value > Max.Value) Max = sample;
            }
            Last = sample;
        }
        public void Include(ProjectionSegment segment)
        {
            if (!_hasSamples) { First = segment.First; Min = segment.Min; Max = segment.Max; _hasSamples = true; }
            else
            {
                if (segment.Min.Value < Min.Value) Min = segment.Min;
                if (segment.Max.Value > Max.Value) Max = segment.Max;
            }
            Last = segment.Last;
        }
    }

}
