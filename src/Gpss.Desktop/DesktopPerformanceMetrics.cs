using System.Diagnostics;

namespace Gpss.Desktop;

/// <summary>Bounded, allocation-free UI timing samples for diagnostics and Windows profiling.</summary>
public static class DesktopPerformanceMetrics
{
    private const int Capacity=256;
    private static readonly long[] UiTicks=new long[Capacity];
    private static readonly long[] RenderTicks=new long[Capacity];
    private static readonly long[] DispatcherDelayTicks=new long[Capacity];
    private static readonly long[] GeometryTicks=new long[Capacity];
    private static int _uiIndex,_renderIndex,_dispatcherIndex,_geometryIndex,_uiCount,_renderCount,_dispatcherCount,_geometryCount;
    private static long _uiUpdates,_historyRenders,_stickRenders,_geometryBuilds,_geometryPoints;

    internal static void RecordUiUpdate(long elapsedTicks,long dispatcherDelayTicks)
    {
        UiTicks[_uiIndex]=elapsedTicks;_uiIndex=(_uiIndex+1)%Capacity;if(_uiCount<Capacity)_uiCount++;
        DispatcherDelayTicks[_dispatcherIndex]=dispatcherDelayTicks;_dispatcherIndex=(_dispatcherIndex+1)%Capacity;if(_dispatcherCount<Capacity)_dispatcherCount++;
        _uiUpdates++;
    }
    internal static void RecordRender(long elapsedTicks,bool history)
    {
        RenderTicks[_renderIndex]=elapsedTicks;_renderIndex=(_renderIndex+1)%Capacity;if(_renderCount<Capacity)_renderCount++;
        if(history)_historyRenders++;else _stickRenders++;
    }
    internal static void RecordGeometry(int pointCount,long elapsedTicks)
    {
        _geometryBuilds++;_geometryPoints=pointCount;
        GeometryTicks[_geometryIndex]=elapsedTicks;_geometryIndex=(_geometryIndex+1)%Capacity;if(_geometryCount<Capacity)_geometryCount++;
    }

    /// <summary>Capture on the UI thread. Percentiles cover the most recent 256 events.</summary>
    public static DesktopPerformanceSnapshot Capture()
    {
        var ui=Summary(UiTicks,_uiCount);var render=Summary(RenderTicks,_renderCount);
        var dispatcher=Summary(DispatcherDelayTicks,_dispatcherCount);var geometry=Summary(GeometryTicks,_geometryCount);
        return new(_uiUpdates,_historyRenders,_stickRenders,_geometryBuilds,_geometryPoints,
            ui.P95,render.P95,dispatcher.P95,geometry.P95,
            ui.P99,ui.Max,render.P99,render.Max,dispatcher.P99,dispatcher.Max,geometry.P99,geometry.Max);
    }

    private static (double P95,double P99,double Max) Summary(long[] source,int count)
    {
        if(count==0)return (0,0,0);
        Span<long> copy=stackalloc long[Capacity];
        source.AsSpan(0,count).CopyTo(copy);
        copy[..count].Sort();
        return (ToMs(copy[Math.Clamp((int)Math.Ceiling(count*.95)-1,0,count-1)]),
            ToMs(copy[Math.Clamp((int)Math.Ceiling(count*.99)-1,0,count-1)]),ToMs(copy[count-1]));
    }
    private static double ToMs(long ticks)=>ticks*1000d/Stopwatch.Frequency;
}

public readonly record struct DesktopPerformanceSnapshot(
    long UiUpdateCount,
    long HistoryRenderCount,
    long StickRenderCount,
    long GeometryBuildCount,
    long LastGeometryPointCount,
    double UiUpdateP95Milliseconds,
    double OnRenderP95Milliseconds,
    double DispatcherDelayP95Milliseconds,
    double GeometryBuildP95Milliseconds,
    double UiUpdateP99Milliseconds,double UiUpdateMaxMilliseconds,
    double OnRenderP99Milliseconds,double OnRenderMaxMilliseconds,
    double DispatcherDelayP99Milliseconds,double DispatcherDelayMaxMilliseconds,
    double GeometryBuildP99Milliseconds,double GeometryBuildMaxMilliseconds);
