using Gpss.Core;
using Gpss.Recording;
using System.Globalization;

namespace Gpss.Cli.Tests;

public sealed class CsvRecordingTests
{
    [Fact]
    public void Legacy_writer_preserves_eight_column_contract_and_missing_values()
    {
        var csv = CsvRecording.WriteLegacy([new(12.5, 0, .25, double.NaN, -.5, .75)]);
        var lines = csv.TrimEnd().Split('\n');
        Assert.Equal("timestamps,elapsed_time,lx,ly,rx,ry,lt,rt", lines[0]);
        Assert.Contains(",NaN,", lines[1]);
        Assert.EndsWith(",NaN,NaN", lines[1]);
    }

    [Fact]
    public void Analyze_ignores_nan_but_counts_rows_and_checks_monotonic_millisecond_time()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "timestamps,elapsed_time,lx,ly,rx,ry,lt,rt\r\n0,0,0,,,,,\r\n1,1,NaN,,,,,\r\n2,2,1,,,,,\r\n");
            var result = CsvRecording.Analyze(path, Axis.LX);
            Assert.Equal(2, result.Count); Assert.Equal(3, result.Rows);
            Assert.Equal(.5, result.Mean); Assert.Equal(.5, result.StdDev); Assert.Equal(.5, result.MaxDeviation);
            Assert.Equal(2, result.DurationMs);
            File.WriteAllText(path, "elapsed_time,lx\n1,0\n0,1\n");
            Assert.Throws<InvalidDataException>(() => CsvRecording.Analyze(path, Axis.LX));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_rejects_declared_non_millisecond_time_unit()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "elapsed_time,elapsed_time_unit,lx\n0,s,0\n");
            Assert.Throws<InvalidDataException>(() => CsvRecording.Analyze(path, Axis.LX));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_rejects_nonfinite_time_and_malformed_row_width()
    {
        var path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"timestamps,elapsed_time,lx,ly,rx,ry,lt,rt\n0,Infinity,0,0,0,0,0,0\n");
            Assert.Throws<InvalidDataException>(()=>CsvRecording.Analyze(path,Axis.LX));
            File.WriteAllText(path,"timestamps,elapsed_time,lx,ly,rx,ry,lt,rt\n0,0,0,0,0,0,0\n");
            Assert.Throws<InvalidDataException>(()=>CsvRecording.Analyze(path,Axis.LX));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void V2_rows_have_fixed_header_width_for_axis_and_auxiliary_records()
    {
        using var writer=new StringWriter(CultureInfo.InvariantCulture);
        CsvRecording.WriteV2Header(writer);
        var device=new DeviceInfo("hid","fixture",BackendKind.HidDirect,[new AxisDescriptor("lx","LX",0,255,8,1,0x30,2,7)]);
        var mapping=new AxisMapping([new(Axis.LX,"lx",true)]);
        var frame=new InputFrame(BackendKind.HidDirect,4,8,200,[new AxisReading(Axis.LX,128,0.0039215686,9)],"fixture_decoder",
            Auxiliary:[new("lt",127,.5,0,255)]);
        CsvRecording.WriteV2(writer,frame,100,1000,device,mapping);
        var lines=writer.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(s=>s.TrimEnd('\r')).ToArray();
        var header=lines[0].Split(',');
        Assert.Equal(25,header.Length);
        Assert.All(lines.Skip(1),line=>Assert.Equal(header.Length,line.Split(',').Length));
        Assert.Contains("axis",lines[1]); Assert.Contains("auxiliary",lines[2]);
        Assert.Contains("fixture_decoder",lines[1]);
    }

    [Fact]
    public void ReadLegacy_preserves_missing_axis_values_as_nan()
    {
        var path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"timestamps,elapsed_time,lx,ly,rx,ry,lt,rt\n0,0,,NaN,0.5,,,\n");
            var row=Assert.Single(CsvRecording.ReadLegacy(path));
            Assert.True(double.IsNaN(row.LX)); Assert.True(double.IsNaN(row.LY));
            Assert.Equal(.5,row.RX); Assert.True(double.IsNaN(row.RY)); Assert.True(double.IsNaN(row.LT));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Analyze_reads_every_preserved_repository_recording()
    {
        var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../data"));
        var files=Directory.GetFiles(directory,"*.csv"); Assert.NotEmpty(files);
        foreach(var path in files)
        foreach(var axis in Enum.GetValues<Axis>())
        {
            var result=CsvRecording.Analyze(path,axis);
            Assert.Equal(10000,result.Rows);
            Assert.InRange(result.Count,0,result.Rows);
            if(result.Count>0) Assert.True(double.IsFinite(result.Mean));
            Assert.True(result.DurationMs>=0);
        }
    }
}
