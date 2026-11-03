namespace Gpss.Core;

/// <summary>Arithmetic mean of unrounded bit values of actual nonzero observed deltas.</summary>
public sealed class MeanBitStepEstimator : IIncrementalStepEstimator
{
    public const string Id = "mean_bits_v1";
    public const int ReferenceCount = 30;
    public string MethodId => Id;
    public IStepEstimatorSession CreateSession() => new Session();
    public StepEstimate Estimate(ReadOnlySpan<double> differences)
    {
        var session = new Session();
        foreach (var delta in differences) session.Add(delta);
        return session.Estimate();
    }

    private sealed class Session : IStepEstimatorSession
    {
        private int _count, _excluded;
        private double _sum, _correction;
        public string MethodId => Id;
        public void Add(double difference) => Change(difference, 1);
        public void Remove(double difference) => Change(difference, -1);
        private void Change(double difference, int direction)
        {
            if (!StepPrecision.TryGetBits(difference, out var bits))
            {
                if (direction < 0 && _excluded == 0) throw new InvalidOperationException("Uncounted excluded edge");
                _excluded += direction; return;
            }
            if (direction < 0 && _count == 0) throw new InvalidOperationException("Uncounted mean edge");
            var value = direction * bits;
            var next = _sum + value;
            _correction += Math.Abs(_sum) >= Math.Abs(value) ? (_sum - next) + value : (value - next) + _sum;
            _sum = next; _count += direction;
            if (_count == 0) _sum = _correction = 0;
        }
        public StepEstimate Estimate()
        {
            if (_count == 0)
                return new(Id, ExcludedCount: _excluded, Message: "変化なし／ゆっくり動かしてください");
            var bits = Math.Max(0, (_sum + _correction) / _count);
            var levels = Math.Pow(2, bits);
            var reference = _count < ReferenceCount;
            // The equivalent step is a geometric mean; it is not the arithmetic mean delta.
            return new(Id, reference ? EstimateStatus.Reference : EstimateStatus.Valid,
                Step: Math.Pow(2, 1 - bits), Levels: double.IsFinite(levels) ? levels : null,
                Bits: bits, NonzeroCount: _count, ExcludedCount: _excluded,
                Message: reference ? $"参考値（非ゼロ差分{ReferenceCount}個未満）" : "");
        }
    }
}
