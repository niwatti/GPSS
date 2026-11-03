using System.Globalization;
using System.Text;
using Gpss.Core;

namespace Gpss.Recording;

/// <summary>CSV interchange for legacy GPSS files and loss-aware GPSS v2 recordings.</summary>
public static class CsvRecording
{
    public static readonly string[] LegacyHeader = ["timestamps", "elapsed_time", "lx", "ly", "rx", "ry", "lt", "rt"];
    public static readonly string[] V2Header = ["schema_version", "timestamp_ms", "elapsed_time_ms", "backend", "mode", "decoder_id", "report_id_usage", "epoch", "sequence", "dropped_count", "record_type", "continuity_id", "axis", "raw_value", "logical_min", "logical_max", "normalized_value", "normalization", "inverted", "link_collection", "auxiliary_name", "auxiliary_raw_value", "auxiliary_value", "auxiliary_logical_min", "auxiliary_logical_max"];

    public static string WriteLegacy(IEnumerable<RecordedRow> rows)
    {
        var sb = new StringBuilder(); WriteLine(sb, LegacyHeader);
        foreach (var row in rows)
            WriteLine(sb, [F(row.TimestampMs), F(row.ElapsedMs), Cell(row, Axis.LX), Cell(row, Axis.LY), Cell(row, Axis.RX), Cell(row, Axis.RY), F(row.LT), F(row.RT)]);
        return sb.ToString();
    }

    public static void WriteLegacyHeader(TextWriter writer) => WriteCsvLine(writer, LegacyHeader);
    public static void WriteLegacyRow(TextWriter writer, RecordedRow row) => WriteCsvLine(writer,
        [row.TimestampMs,row.ElapsedMs,row.LX,row.LY,row.RX,row.RY,row.LT,row.RT]);

    public static void WriteV2Header(TextWriter writer) => WriteCsvLine(writer, V2Header);
    public static void WriteV2(TextWriter writer, InputFrame frame, long elapsedTicks, long frequency, DeviceInfo device, AxisMapping? mapping = null)
    {
        var descriptors = device.Axes.ToDictionary(a => a.Key);
        Dictionary<Axis, string> bindings = mapping is null
            ? new Dictionary<Axis, string>() : mapping.Bindings.ToDictionary(b => b.Axis, b => b.DescriptorKey);
        AxisDescriptor? Descriptor(AxisReading reading) => bindings.TryGetValue(reading.Axis, out var key) && descriptors.TryGetValue(key, out var found) ? found : null;
        var usageParts=frame.Readings.Select(r => Descriptor(r) is { } d
            ? $"{r.Axis}:page={d.UsagePage}:usage={d.Usage}:link={d.LinkCollection}:report={d.ReportId}" : $"{r.Axis}:unknown").ToList();
        if(mapping is not null && !mapping.AuxiliaryBindings.IsDefaultOrEmpty && !frame.Auxiliary.IsDefaultOrEmpty)
            foreach(var reading in frame.Auxiliary)
                foreach(var binding in mapping.AuxiliaryBindings.Where(b=>b.Name.Equals(reading.Name,StringComparison.OrdinalIgnoreCase)))
                    if(descriptors.TryGetValue(binding.DescriptorKey,out var d)) usageParts.Add($"{reading.Name}:page={d.UsagePage}:usage={d.Usage}:link={d.LinkCollection}:report={d.ReportId}");
        var usage=string.Join(";",usageParts);
        var baseRow = new object?[] { "2", Ms(frame.Timestamp, frequency), Ms(elapsedTicks, frequency), frame.Backend, frame.Mode, frame.DecoderId, usage,
            frame.Epoch, frame.Sequence, frame.DroppedCount };
        if (frame.Readings.IsDefaultOrEmpty && frame.Auxiliary.IsDefaultOrEmpty)
        { WriteCsvLine(writer, [..baseRow, "frame", "", "", "", "", "", "", "linear_minmax_v1", "", "", "", "", "", "", ""]); }
        else foreach (var reading in frame.Readings)
        {
            var desc = Descriptor(reading);
            bool inverted=bindings.TryGetValue(reading.Axis,out var key)&&mapping!.Bindings.Any(b=>b.Axis==reading.Axis&&b.DescriptorKey==key&&b.Inverted);
            WriteCsvLine(writer, [..baseRow, "axis", reading.ContinuityId, reading.Axis.ToString().ToLowerInvariant(), reading.RawValue,
                desc?.LogicalMin, desc?.LogicalMax, reading.Value, "linear_minmax_v1", inverted, desc?.LinkCollection, "", "", "", "", ""]);
        }
        if(!frame.Auxiliary.IsDefaultOrEmpty) foreach(var aux in frame.Auxiliary)
        {
            var inverted=mapping is not null && !mapping.AuxiliaryBindings.IsDefaultOrEmpty && mapping.AuxiliaryBindings.Any(b=>b.Name.Equals(aux.Name,StringComparison.OrdinalIgnoreCase)&&b.Inverted);
            WriteCsvLine(writer,[..baseRow,"auxiliary","", "",aux.RawValue,aux.LogicalMin,aux.LogicalMax,aux.Value,"linear_minmax_v1",inverted,"",aux.Name,aux.RawValue,aux.Value,aux.LogicalMin,aux.LogicalMax]);
        }
    }

    public static CsvAnalysis Analyze(string path, Axis axis)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var records = ReadRecords(reader).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("CSV is empty");
        var header = records.Current.Select((name, i) => (name: name.Trim().TrimStart('\uFEFF').ToLowerInvariant(), i)).ToDictionary(x => x.name, x => x.i);
        if (!header.TryGetValue("elapsed_time", out var timeCol) || !header.TryGetValue(axis.ToString().ToLowerInvariant(), out var axisCol) ||
            !header.ContainsKey("timestamps") || !header.ContainsKey("lx") || !header.ContainsKey("ly") || !header.ContainsKey("rx") || !header.ContainsKey("ry") || !header.ContainsKey("lt") || !header.ContainsKey("rt"))
            throw new InvalidDataException("CSV must contain the legacy timestamps, elapsed_time, lx, ly, rx, ry, lt, rt header");
        // The old GPSS writer stores elapsed_time in milliseconds (measured from the
        // measurement start); v2 names the field elapsed_time_ms explicitly.
        header.TryGetValue("elapsed_time_unit", out var unitCol);
        var hasUnit = header.ContainsKey("elapsed_time_unit");
        header.TryGetValue("timestamp_unit",out var timestampUnitCol);
        var hasTimestampUnit=header.ContainsKey("timestamp_unit");
        var values = new List<double>(); var times = new List<double>(); var timestamps=new List<double>();
        while (records.MoveNext())
        {
            var row = records.Current; if (row.Count != header.Count) throw new InvalidDataException("CSV row length does not match its header");
            if (hasUnit && (unitCol >= row.Count || !(row[unitCol].Equals("ms", StringComparison.OrdinalIgnoreCase) || row[unitCol].Equals("milliseconds", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("elapsed_time_unit must be ms or milliseconds");
            if (hasTimestampUnit && (timestampUnitCol >= row.Count || !(row[timestampUnitCol].Equals("ms", StringComparison.OrdinalIgnoreCase) || row[timestampUnitCol].Equals("milliseconds", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("timestamp_unit must be ms or milliseconds");
            var time = Parse(row[timeCol]);
            var timestamp=Parse(row[header["timestamps"]]);
            if (!double.IsFinite(time)||!double.IsFinite(timestamp)) throw new InvalidDataException("CSV times must be finite milliseconds");
            if (time < 0 || timestamp < 0) throw new InvalidDataException("CSV times must be nonnegative milliseconds");
            if (times.Count > 0 && time < times[^1]) throw new InvalidDataException("elapsed_time must be nondecreasing");
            if(timestamps.Count>0 && timestamp<timestamps[^1]) throw new InvalidDataException("timestamps must be nondecreasing milliseconds");
            times.Add(time); timestamps.Add(timestamp);
            var cell = row[axisCol].Trim();
            var value = cell.Length == 0 || cell.Equals("NaN", StringComparison.OrdinalIgnoreCase) ? double.NaN : Parse(cell);
            if (!double.IsFinite(value) && !double.IsNaN(value)) throw new InvalidDataException("Axis values must be finite numbers, NaN, or empty");
            values.Add(value);
        }
        var finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return new(0, double.NaN, double.NaN, double.NaN, times.Count, 0);
        var mean = finite.Average(); var variance = finite.Sum(v => (v - mean) * (v - mean)) / finite.Length;
        return new(finite.Length, Math.Sqrt(variance), mean, finite.Max(v => Math.Abs(v - mean)), times.Count,
            times.Count > 1 ? times[^1] - times[0] : 0);
    }

    public static IReadOnlyList<RecordedRow> ReadLegacy(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true); var all = ReadRecords(reader).ToList();
        if (all.Count == 0) return [];
        var h = all[0].Select((x,i)=>(x:x.Trim().ToLowerInvariant(),i)).ToDictionary(x=>x.x,x=>x.i);
        string[] required=["timestamps","elapsed_time","lx","ly","rx","ry","lt","rt"];
        if(required.Any(name=>!h.ContainsKey(name))) throw new InvalidDataException("CSV is missing a required legacy column");
        int Id(string s) => h.TryGetValue(s, out var i) ? i : -1;
        var result = new List<RecordedRow>();
        foreach (var r in all.Skip(1))
        {
            string Get(string col) { var i=Id(col); return i >= 0 && i < r.Count ? r[i] : ""; }
            if(r.Count != all[0].Count) throw new InvalidDataException("CSV row length does not match its header");
            result.Add(new(ParseOrNaN(Get("timestamps")), ParseOrNaN(Get("elapsed_time")),
                ParseOrNaN(Get("lx")), ParseOrNaN(Get("ly")), ParseOrNaN(Get("rx")), ParseOrNaN(Get("ry")), ParseOrNaN(Get("lt")), ParseOrNaN(Get("rt"))));
        }
        return result;
    }

    private static string Cell(RecordedRow row, Axis axis) => F(axis switch { Axis.LX=>row.LX, Axis.LY=>row.LY, Axis.RX=>row.RX, _=>row.RY });
    private static double ParseOrNaN(string s) => string.IsNullOrWhiteSpace(s) ? double.NaN : Parse(s);
    private static double Parse(string s)
    {
        if(!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var value))
            throw new InvalidDataException($"Invalid numeric CSV cell: {s}");
        if(double.IsInfinity(value)) throw new InvalidDataException("CSV numeric values cannot be infinite");
        return value;
    }
    private static string F(double n) => double.IsNaN(n) ? "NaN" : n.ToString("R", CultureInfo.InvariantCulture);
    private static string F(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Ms(long ticks, long frequency) => (ticks * 1000d / frequency).ToString("R", CultureInfo.InvariantCulture);
    private static void WriteLine(StringBuilder sb, IEnumerable<string> cells) => sb.AppendLine(string.Join(",", cells.Select(Escape)));
    private static void WriteCsvLine(TextWriter writer, IEnumerable<object?> cells)
    {
        var array=cells.Select(x=>Escape(x is null ? "" : Convert.ToString(x,CultureInfo.InvariantCulture)??"")).ToArray();
        if(array.Length!=V2Header.Length && array.Length!=LegacyHeader.Length) throw new InvalidDataException($"Unexpected CSV column count: {array.Length}");
        writer.WriteLine(string.Join(",",array));
    }
    private static string Escape(string s) => s.IndexOfAny([',','"','\r','\n']) < 0 ? s : '"' + s.Replace("\"", "\"\"") + '"';
    private static IEnumerable<List<string>> ReadRecords(TextReader reader)
    {
        var row = new List<string>(); var cell = new StringBuilder(); bool quoted=false, quoteClosed=false;
        while (true)
        {
            int n=reader.Read(); if(n<0) { if(quoted) throw new InvalidDataException("Unclosed quoted CSV cell"); if(cell.Length>0||row.Count>0||quoteClosed){row.Add(cell.ToString());yield return row;} yield break; }
            char c=(char)n;
            if(quoted) { if(c=='"') { if(reader.Peek()=='"'){reader.Read();cell.Append('"');} else {quoted=false;quoteClosed=true;} } else cell.Append(c); continue; }
            if(quoteClosed && c is not (',' or '\r' or '\n')) throw new InvalidDataException("Unexpected character after quoted CSV cell");
            if(c=='"') { if(cell.Length!=0||quoteClosed) throw new InvalidDataException("Quote inside an unquoted CSV cell"); quoted=true;continue; }
            if(c==','){row.Add(cell.ToString());cell.Clear();quoteClosed=false;continue;}
            if(c=='\r')
            {
                if(reader.Peek()=='\n')reader.Read();
                if(row.Count!=0||cell.Length!=0||quoteClosed){row.Add(cell.ToString());cell.Clear();yield return row;row=[];}
                quoteClosed=false; continue;
            }
            if(c=='\n')
            {
                // Ignore blank records. Older Windows-generated GPSS CSVs contain
                // CR CR LF line endings, which otherwise look like a blank row.
                if(row.Count!=0||cell.Length!=0||quoteClosed){row.Add(cell.ToString());cell.Clear();yield return row;row=[];}
                quoteClosed=false; continue;
            }
            cell.Append(c);
        }
    }
}

public sealed record RecordedRow(double TimestampMs,double ElapsedMs,double LX,double LY,double RX,double RY,double LT=double.NaN,double RT=double.NaN);
public sealed record CsvAnalysis(int Count,double StdDev,double Mean,double MaxDeviation,int Rows,double DurationMs);
