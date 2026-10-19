using FireGaze.PortraitPreview;

static class LightInputTraceCases
{
    public static void Run(Action<bool,string> check)
    {
#if LIGHTINPUTTRACE
        var messages=new List<string>();
        var trace=new PortraitLightInputTrace(messages.Add,maximumStages:4);
        using (var ignored=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeSetAngle)) ignored.Complete();
        check(messages.Count==0,"native light calls outside explicit input do not emit trace");
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>
        {
            check(messages.Count==1 && messages[0].Contains("GestureEnd begin"),"gesture entry is emitted before work begins");
            using var nested=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeSetAngle);
            check(messages.Count==2 && messages[1].Contains("NativeSetAngle begin"),"nested native entry is observable while parent is incomplete");
            nested.Complete();
        });
        check(messages.Count==4 && messages[2].Contains("NativeSetAngle end") && messages[3].Contains("GestureEnd end"),
            "successful nested stages complete before their parent");
        messages.Clear();
        var failure=new InvalidOperationException("sensitive exception message");
        try { trace.Enter(PortraitLightTraceStage.GestureMove,()=>throw failure); }
        catch (Exception actual) { check(ReferenceEquals(actual,failure),"trace preserves the original operation exception"); }
        check(messages.Count==2 && messages[1].Contains("GestureMove failed") && !string.Join("",messages).Contains("sensitive"),
            "exceptional work is distinct from success without recording exception content");
        messages.Clear();
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>
        {
            for(var i=0;i<50;i++) PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryPoll,()=>{});
        });
        check(messages.Count==3 && messages.Count(x=>x.Contains("budget-exhausted"))==1 && messages[^1].Contains("GestureEnd end"),
            "bounded trace reserves the outer end and emits one exhaustion marker");
        var count=messages.Count;
        trace.Enter(PortraitLightTraceStage.GestureEnd,()=>{});
        check(messages.Count==count,"exhausted gestures cannot log continuously on later frames");

        var broken=new PortraitLightInputTrace(_=>throw new IOException("log unavailable"));
        var ran=0;
        broken.Enter(PortraitLightTraceStage.GestureMove,()=>
            PortraitLightInputTrace.Run(PortraitLightTraceStage.NativeSetAngle,()=>ran++));
        check(ran==1,"begin and end logger failures cannot interrupt the wrapped native operation");
        messages.Clear();
        var fresh=new PortraitLightInputTrace(messages.Add);
        fresh.Enter(PortraitLightTraceStage.GestureMove,()=>
        {
            try { broken.Enter(PortraitLightTraceStage.GestureMove,()=>throw failure); } catch(InvalidOperationException) { }
            PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryFlush,()=>{});
        });
        check(messages.Count==4 && messages[1].Contains("HistoryFlush begin") && messages[^1].Contains("GestureMove end"),
            "nested trace failure restores the prior ambient gesture");
        var escaped=messages.Count;
        PortraitLightInputTrace.Run(PortraitLightTraceStage.NativeSetAngle,()=>ran++);
        check(messages.Count==escaped && ran==2,"completed gesture restores untraced execution between framework calls");
        messages.Clear();
        fresh.Enter(PortraitLightTraceStage.GestureMove,()=>
        {
            var scope=PortraitLightInputTrace.Measure(PortraitLightTraceStage.NativeSetAngle);
            scope.Complete();scope.Dispose();scope.Dispose();
        });
        check(messages.Count==4,"duplicate scope disposal cannot emit duplicate completion");
#else
        check(false,"explicit light input trace is absent; hangs and exceptions lack bounded native phase evidence");
#endif
    }
}
