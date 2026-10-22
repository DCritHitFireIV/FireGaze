using FireGaze.PortraitPreview;

static unsafe class LightInputNativeTraceCases
{
    public static void Run(Action<bool,string> check)
    {
#if LIGHTINPUTTRACE
        using var fixture=new EditorFixture();
        using var target=new NativePortraitTarget(fixture,_=>false,_=>{});
        check(target.TryResolve(out var identity,out _,out _),"trace fixture resolves actual native portrait structures");
        var messages=new List<string>();var trace=new PortraitLightInputTrace(messages.Add);
        var accepted=false;
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>accepted=target.SetDirectionalLightAngles(identity,new(0,0),new(30,-45)));
        check(accepted && fixture.LightSyncCalls==0,
            "real narrow angle setter leaves UI slider synchronization outside the native write transaction");
        string[] expected=["GestureEnd begin","NativeExportBefore begin","NativeExportBefore end","NativeSetAngle begin","NativeSetAngle end",
            "NativeExportAfter begin","NativeExportAfter end","BannerEntryWrite begin","BannerEntryWrite end","GestureEnd end"];
        check(messages.Select(x=>x[(x.IndexOf(" phase=",StringComparison.Ordinal)+7)..]).SequenceEqual(expected),
            "actual native setter exposes each completed boundary in production call order");
        messages.Clear();fixture.ThrowLightSync=true;
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>accepted=target.SetDirectionalLightAngles(identity,new(30,-45),new(40,50)));
        check(accepted && !messages.Any(x=>x.Contains("NativeSyncControls")),
            "native angle write does not invoke a reentrant slider callback");
        messages.Clear();fixture.ThrowLightSync=false;fixture.ThrowDirectionalSet=true;
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>accepted=target.SetDirectionalLightAngles(identity,new(40,50),new(60,70)));
        check(!accepted && messages.Any(x=>x.EndsWith("NativeSetAngle failed")) && !messages.Any(x=>x.Contains("NativeExportAfter")),
            "failed native setter ends its own phase and never reports an unreached export");
#else
        check(false,"real native light setter lacks explicit phase instrumentation");
#endif
    }
}
