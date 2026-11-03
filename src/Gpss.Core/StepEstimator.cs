namespace Gpss.Core;

public interface IStepEstimator
{
    string MethodId { get; }
    StepEstimate Estimate(ReadOnlySpan<double> differences);
}

/// <summary>Optional add/expire path for estimators that can maintain a window session incrementally.</summary>
public interface IIncrementalStepEstimator : IStepEstimator
{
    IStepEstimatorSession CreateSession();
}

public interface IStepEstimatorSession
{
    string MethodId { get; }
    void Add(double difference);
    void Remove(double difference);
    StepEstimate Estimate();
}

public sealed class EstimatorRegistry
{
    private readonly Dictionary<string, Func<IStepEstimator>> _factories = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Ids => _factories.Keys.ToArray();

    public void Register(string id, Func<IStepEstimator> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(id, factory)) throw new ArgumentException("Estimator ID is already registered", nameof(id));
    }

    public IStepEstimator Create(string id)
    {
        var estimator = _factories[id]();
        if (estimator.MethodId != id) throw new InvalidOperationException("Estimator ID does not match registry key");
        return estimator;
    }

    public static EstimatorRegistry CreateDefault()
    {
        var registry = new EstimatorRegistry();
        registry.Register(MeanBitStepEstimator.Id, () => new MeanBitStepEstimator());
        return registry;
    }
}

public sealed record ModalStepConfig(int Version = 1, double Quantum = 0.0000001, int ReferenceCount = 30);

public sealed class ModalStepEstimator : IIncrementalStepEstimator
{
    public const string Id = "modal_delta_v1";
    public string MethodId => Id;
    public ModalStepConfig Config { get; }

    public ModalStepEstimator(ModalStepConfig? config = null)
    {
        Config = config ?? new();
        if (Config.Version != 1 || !double.IsFinite(Config.Quantum) || Config.Quantum <= 0 || Config.ReferenceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(config));
    }

    public IStepEstimatorSession CreateSession() => new ModalStepSession(Config);

    public StepEstimate Estimate(ReadOnlySpan<double> differences)
    {
        var buckets = new Dictionary<double, ModalBucket>();
        var excluded = 0;
        var count = 0;
        foreach (var delta in differences)
        {
            var scaled = delta / Config.Quantum;
            if (!double.IsFinite(delta) || delta <= 0 || delta > 2 || !double.IsFinite(scaled))
            {
                excluded++;
                continue;
            }
            var key = Math.Floor(scaled + 0.5);
            if (key == 0) { excluded++; continue; }
            if (!buckets.TryGetValue(key, out var bucket)) buckets.Add(key, bucket = new());
            bucket.Add(delta);
            count++;
        }
        if (count == 0)
            return new(MethodId, ExcludedCount: excluded, Message: "変化なし／ゆっくり動かしてください");
        var maxCount = 0; var winnerKey = double.PositiveInfinity; var winner = default(ModalBucket); var winnerCount = 0;
        foreach (var pair in buckets)
        {
            if (pair.Value.Count > maxCount)
            { maxCount = pair.Value.Count; winnerKey = pair.Key; winner = pair.Value; winnerCount = 1; }
            else if (pair.Value.Count == maxCount)
            {
                winnerCount++;
                if (pair.Key < winnerKey) { winnerKey = pair.Key; winner = pair.Value; }
            }
        }
        if (winner is null) throw new InvalidOperationException("No modal bucket was selected");
        var step = (winner.Sum + winner.Correction) / winner.Count;
        var (levels, bits) = StepPrecision.Convert(step);
        var reference = count < Config.ReferenceCount;
        var tie = winnerCount > 1;
        var message = reference && tie ? $"参考値（非ゼロ差分{Config.ReferenceCount}個未満）／最頻値が同率"
            : reference ? $"参考値（非ゼロ差分{Config.ReferenceCount}個未満）" : tie ? "最頻値が同率" : "";
        return new(MethodId, reference ? EstimateStatus.Reference : EstimateStatus.Valid,
            step, levels, bits, count, maxCount, maxCount / (double)count, tie, excluded, message);
    }

    public sealed class ModalStepSession : IStepEstimatorSession
    {
        private readonly ModalStepConfig _config;
        private readonly Dictionary<double, Bucket> _buckets = [];
        private readonly SortedSet<Rank> _ranked = new(RankComparer.Instance);
        public string MethodId => ModalStepEstimator.Id;
        public int ExcludedCount { get; private set; }
        public int NonzeroCount { get; private set; }

        public ModalStepSession(ModalStepConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.Version != 1 || !double.IsFinite(config.Quantum) || config.Quantum <= 0 || config.ReferenceCount < 1)
                throw new ArgumentOutOfRangeException(nameof(config));
            _config = config;
        }
        public void Add(double difference) => Update(difference, +1);
        public void Remove(double difference) => Update(difference, -1);
        private void Update(double difference, int direction)
        {
            var scaled = difference / _config.Quantum;
            if (!double.IsFinite(difference) || difference <= 0 || difference > 2 || !double.IsFinite(scaled))
            { ExcludedCount += direction; return; }
            var key = Math.Floor(scaled + 0.5);
            if (key == 0) { ExcludedCount += direction; return; }
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (direction < 0) throw new InvalidOperationException("Modal edge expired before it was indexed");
                _buckets.Add(key, bucket = new());
            }
            if (bucket.Count > 0) _ranked.Remove(new(bucket.Count, key));
            bucket.Add(direction * difference); bucket.Count += direction; NonzeroCount += direction;
            if (bucket.Count <= 0) _buckets.Remove(key);
            else _ranked.Add(new(bucket.Count, key));
        }

        public StepEstimate Estimate()
        {
            if (_ranked.Count == 0)
                return new(MethodId, ExcludedCount: ExcludedCount, Message: "変化なし／ゆっくり動かしてください");
            var ranked = _ranked.GetEnumerator(); ranked.MoveNext();
            var winner = ranked.Current; var hasSecond = ranked.MoveNext();
            var bucket = _buckets[winner.Key];
            var step = (bucket.Sum + bucket.Correction) / bucket.Count;
            var (levels, bits) = StepPrecision.Convert(step);
            var tie = hasSecond && ranked.Current.Count == winner.Count;
            var reference = NonzeroCount < _config.ReferenceCount;
            var message = reference && tie ? $"参考値（非ゼロ差分{_config.ReferenceCount}個未満）／最頻値が同率"
                : reference ? $"参考値（非ゼロ差分{_config.ReferenceCount}個未満）"
                : tie ? "最頻値が同率" : "";
            return new(MethodId, reference ? EstimateStatus.Reference : EstimateStatus.Valid,
                step, levels, bits, NonzeroCount, winner.Count, winner.Count / (double)NonzeroCount,
                tie, ExcludedCount, message);
        }

        private sealed class Bucket
        {
            public int Count;
            public double Sum, Correction;
            public void Add(double value)
            {
                var next = Sum + value;
                Correction += Math.Abs(Sum) >= Math.Abs(value) ? (Sum - next) + value : (value - next) + Sum;
                Sum = next;
            }
        }
        private readonly record struct Rank(int Count, double Key);
        private sealed class RankComparer : IComparer<Rank>
        {
            public static RankComparer Instance { get; } = new();
            public int Compare(Rank x, Rank y)
            { var c = y.Count.CompareTo(x.Count); return c != 0 ? c : x.Key.CompareTo(y.Key); }
        }
    }

    private sealed class ModalBucket
    {
        public int Count;
        public double Sum, Correction;
        public void Add(double value)
        {
            var next = Sum + value;
            Correction += Math.Abs(Sum) >= Math.Abs(value)
                ? (Sum - next) + value : (value - next) + Sum;
            Sum = next; Count++;
        }
    }
}
