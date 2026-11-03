using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Gpss.Core;

namespace Gpss.Desktop;

/// <summary>Single cached plot combining the bit distribution of all four stick axes.</summary>
public sealed class StepHistogramControl : Border
{
    private readonly TextBlock _caption = new()
    {
        FontSize = 11, Foreground = Brushes.SlateGray, Margin = new(0, 1, 0, 3),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly HistogramPlot _plot = new();
    private StepHistogramSnapshot? _snapshot;

    public StepHistogramControl()
    {
        Height = 154; Margin = new(4); Padding = new(10, 6, 10, 5);
        Background = Brushes.White; BorderBrush = Brushes.LightGray;
        BorderThickness = new(1); CornerRadius = new(8);
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = "ステップ精度ヒストグラム — 左X・左Y・右X・右Yの合計",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkSlateGray
        });
        Grid.SetRow(_caption, 1); grid.Children.Add(_caption);
        Grid.SetRow(_plot, 2); grid.Children.Add(_plot); Child = grid;
        UpdateCaption(null);
    }

    public void Render(StepHistogramSnapshot? snapshot)
    {
        if (ReferenceEquals(snapshot, _snapshot)) return;
        _snapshot = snapshot; UpdateCaption(snapshot);
        _plot.Set(snapshot?.Bins ?? [], snapshot?.TotalCount ?? 0);
    }

    private void UpdateCaption(StepHistogramSnapshot? snapshot)
    {
        var period = snapshot?.WindowSeconds is double seconds ? $"最新{seconds:0.#}秒" : "リセット後の累計";
        var text = snapshot is null ? "有効なステップ精度データを待っています。横軸: bit / 縦軸: 件数" :
            $"{snapshot.ValueLabel} / {period} / {snapshot.TotalCount:N0}件 / 1bit幅（8〜8.9、9〜9.9…）";
        if (_caption.Text != text) _caption.Text = text;
    }

    private sealed class HistogramPlot : FrameworkElement
    {
        private static readonly Typeface Typeface = new("Segoe UI");
        private static readonly Brush BarBrush = FrozenBrush(Color.FromRgb(79, 70, 229));
        private static readonly Brush LabelBrush = FrozenBrush(Color.FromRgb(71, 85, 105));
        private static readonly Pen GridPen = FrozenPen(Color.FromRgb(226, 232, 240));
        private ImmutableArray<StepHistogramBin> _bins = [];
        private DrawingGroup? _drawing;
        private long _total;
        private bool _dirty = true;
        private Size _builtSize;
        private double _builtDpi;
        private int _firstBin, _lastBin = 16;
        private long _hoveredBin = -1;

        public HistogramPlot()
        {
            MinHeight = 76; ClipToBounds = true;
            IsVisibleChanged += (_, _) => { if (IsVisible) InvalidateVisual(); };
        }

        public void Set(ImmutableArray<StepHistogramBin> bins, long total)
        {
            if (total == _total && bins.AsSpan().SequenceEqual(_bins.AsSpan())) return;
            _bins = bins; _total = total; _dirty = true;
            if (_hoveredBin >= 0) UpdateTooltip(_hoveredBin);
            if (IsVisible) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_dirty || _drawing is null || _builtSize != RenderSize || _builtDpi != dpi) Build(dpi);
            if (_drawing is not null) dc.DrawDrawing(_drawing);
        }

        private void Build(double dpi)
        {
            var left = 48d; var right = Math.Max(left + 1, ActualWidth - 8);
            var top = 4d; var bottom = Math.Max(top + 1, ActualHeight - 25);
            _firstBin = _bins.IsDefaultOrEmpty ? 0 : Math.Max(0, _bins[0].LowerBit - 1);
            _lastBin = _bins.IsDefaultOrEmpty ? 16 : Math.Max(_firstBin + 4, _bins[^1].LowerBit + 1);
            var span = (long)_lastBin - _firstBin + 1;
            var slot = (right - left) / span;
            var max = 4d;
            foreach (var bin in _bins) max = Math.Max(max, bin.Count);
            max = Math.Ceiling(max / 4) * 4;
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
                for (var tick = 0; tick <= 4; tick++)
                {
                    var y = bottom - (bottom - top) * tick / 4;
                    dc.DrawLine(GridPen, new(left, y), new(right, y));
                    Text(dc, (max * tick / 4).ToString("N0", CultureInfo.CurrentCulture), new(0, y - 6), dpi);
                }
                foreach (var bin in _bins)
                {
                    var height = bin.Count / max * (bottom - top);
                    if (height <= 0) continue;
                    var x = left + (bin.LowerBit - (long)_firstBin) * slot;
                    var padding = Math.Min(3, slot / 6);
                    dc.DrawRectangle(BarBrush, null, new Rect(x + padding, bottom - height,
                        Math.Max(.25, slot - padding * 2), height));
                }
                var stride = Math.Max(1L, (long)Math.Ceiling(span / Math.Max(1, (right - left) / 64)));
                for (long bit = _firstBin; bit <= _lastBin; bit += stride)
                {
                    var x = left + (bit - _firstBin + .5) * slot;
                    Text(dc, $"{bit}〜{bit}.9", new(Math.Clamp(x - 19, left, Math.Max(left, right - 48)), bottom + 4), dpi);
                }
                if (_total == 0) Text(dc, "データなし", new(left + 8, top + 8), dpi);
            }
            drawing.Freeze(); _drawing = drawing; _builtSize = RenderSize; _builtDpi = dpi; _dirty = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = e.GetPosition(this); var width = ActualWidth - 56;
            if (width <= 0 || point.X < 48 || point.X > ActualWidth - 8) { ToolTip = null; _hoveredBin = -1; return; }
            var span = (long)_lastBin - _firstBin + 1;
            var bit = Math.Min(_lastBin, _firstBin + (long)((point.X - 48) / width * span));
            if (bit == _hoveredBin) return;
            _hoveredBin = bit; UpdateTooltip(bit);
        }

        private void UpdateTooltip(long bit)
        {
            long count = 0;
            foreach (var bin in _bins) if (bin.LowerBit == bit) { count = bin.Count; break; }
            ToolTip = $"{bit}〜{bit}.9 bit: {count:N0}件" + (_total > 0 ? $"（{count / (double)_total:P1}）" : "");
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        { base.OnMouseLeave(e); ToolTip = null; _hoveredBin = -1; }

        private static void Text(DrawingContext dc, string text, Point point, double dpi)
            => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface, 10, LabelBrush, dpi), point);
        private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        private static Pen FrozenPen(Color color) { var pen = new Pen(FrozenBrush(color), 1); pen.Freeze(); return pen; }
    }
}
