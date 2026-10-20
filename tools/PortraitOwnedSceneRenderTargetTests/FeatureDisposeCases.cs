using FireGaze.PortraitPreview;

internal static class FeatureDisposeCases
{
    internal static async Task Run()
    {
        var feature=new PortraitPreviewFeature();
        feature.Start(); feature.Source(1); feature.Source(2);
        feature.Dispose();
        if(feature.SourceEpoch!=7 || !feature.SourceAlive || feature.NativeReleased || feature.ManagedSealed)
            throw new Exception("actual Feature Dispose invalidated source roots/epoch or sealed writers before owned retirement");
        var physical=feature.PhysicalDrain;
        if(physical.IsCompleted || feature.ManagedDrain.IsCompleted)
            throw new Exception("actual Feature Dispose reported completion before future native source return");
        feature.Source(3); // The Feature's Update was detached: no manual Update call.
        await physical.WaitAsync(TimeSpan.FromSeconds(2));
        await feature.ManagedDrain.WaitAsync(TimeSpan.FromSeconds(2));
        if(feature.SourceEpoch!=8 || feature.SourceAlive || !feature.NativeReleased || !feature.ManagedSealed
            || !feature.Trace.SequenceEqual(new[]{"stop-owned","native-retired","dispose-opening","dispose-census","close-source","dispose-target","seal-managed"}))
            throw new Exception($"actual Feature deferred release sequence did not retain and then release exact source: epoch={feature.SourceEpoch} alive={feature.SourceAlive} native={feature.NativeReleased} managed={feature.ManagedSealed} trace="+string.Join(",",feature.Trace));
        Console.WriteLine("PASS actual Feature Dispose retains source roots/epoch through future native retirement and drains without Update");
    }
}

namespace FireGaze
{
    internal static class Plugin
    {
        internal static readonly DisposalFramework Framework=new();
        internal static readonly DisposalLog Log=new();
    }
    internal sealed class DisposalFramework
    {
        internal bool IsInFrameworkUpdateThread=>true;
        internal event Action? Update;
        internal void Raise()=>Update?.Invoke();
        internal Task RunOnFrameworkThread(Action action){action();return Task.CompletedTask;}
    }
    internal sealed class DisposalLog
    {
        internal void Error(string text,object? value){}
        internal void Warning(Exception error,string text){}
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private readonly OwnedExperimentBrokerCases.Fixture fixture;
        private bool disposed;
        private Task ownedViewportDrain=Task.CompletedTask;
        private Task? managedDrain,physicalDrain;
        private readonly TaskCompletionSource incompletePhysicalClose=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DisposalObject photos=new(),window=new(),diagnostics=new(),windows=new();
        private readonly DisposalObject cacheWriter=new(),history=new();
        private readonly DisposalInterface pluginInterface=new();
        private readonly DisposalTarget target;
        private readonly DisposalSession session;
        internal long SourceEpoch=7;
        internal bool SourceAlive=true;
        internal bool ManagedSealed=>cacheWriter.Sealed&&history.Sealed;
        internal bool NativeReleased=>fixture.Runtime.Retires==1;
        internal readonly List<string> Trace=[];
        internal Task PhysicalDrain=>physicalDrain!;
        internal Task ManagedDrain=>managedDrain!;
        internal PortraitPreviewFeature()
        {
            fixture=new(true,requestCurrent:()=>SourceEpoch==7&&SourceAlive);
            fixture.Runtime.AfterRetire=()=>Trace.Add("native-retired");
            target=new(this);session=new(this);
        }
        internal void Start(){if(!fixture.Controller.TryStart(fixture.SourceSnapshot,3))throw new Exception("fixture start");}
        internal void Source(uint frame)
        {
            fixture.Render(frame);
        }
        internal void Dispose()
        {
            if(disposed)return; disposed=true;
            StopOwnedViewport("Feature disposal is waiting for the independent scene to retire.");
            Task release=ownedViewportDrain.IsCompletedSuccessfully?Task.Run(Release):ownedViewportDrain.ContinueWith(_=>Release());
            managedDrain=PortraitCaptureRetirement.CombineOwned(this,release.ContinueWith(_=>
            {
                cacheWriter.BeginDispose(); history.BeginDispose();
            }));
            physicalDrain=PortraitCaptureRetirement.CombineOwned(this,release);
        }
        private void Release()
        {
            Trace.Add("dispose-opening");Trace.Add("dispose-census");SourceEpoch++;
            Trace.Add("close-source");SourceAlive=false;Trace.Add("dispose-target");
            Trace.Add("seal-managed");
        }
        private void StopOwnedViewport(string reason)
        {Trace.Add("stop-owned");fixture.Controller.Dispose();fixture.Broker.Dispose();ownedViewportDrain=fixture.Broker.DrainPhysicalAsync();}
        private void DisposeEditorOpening()=>Trace.Add("dispose-opening");
        private void DisposeSourceCensus(){Trace.Add("dispose-census");SourceEpoch++;}
        private static bool CancelLightingFitForEdit()=>true;
        private static void DisposeNativeLive(){}
        private static void DisposeLightingFit(){}
        private static void FlushEditing(){}
        private static void QueueOwnedCacheIfNeeded(){}
        private static void DisposeLibrary(){}
        private static Task DrainFavoritePersistence()=>Task.CompletedTask;
        private static Task DrainStorageObservation()=>Task.CompletedTask;
        private static void DisposeCaptureProgress(){}
        private static void Update(){}
        private static void Draw(){}
        private sealed class DisposalTarget(PortraitPreviewFeature owner)
        {
            internal string? LastError=>null;
            internal bool AbortCapturedEditor()=>true;
            internal void Dispose(){owner.Trace.Add("dispose-target");}
        }
        private sealed class DisposalSession(PortraitPreviewFeature owner)
        {
            internal bool Close(){owner.Trace.Add("close-source");owner.SourceAlive=false;return true;}
        }
        private sealed class DisposalInterface{internal readonly DisposalUI UiBuilder=new();}
        private sealed class DisposalUI{internal event Action? Draw;internal void Raise()=>Draw?.Invoke();}
        private sealed class DisposalObject:IDisposable
        {
            internal bool Sealed;
            internal Action? OnBegin { get; set; }
            internal Task BeginDispose(){Sealed=true;OnBegin?.Invoke();return Task.CompletedTask;}
            internal Task DrainPhysicalAsync()=>Task.CompletedTask;
            public void Dispose(){}
            internal void UnregisterFunc(){}
            internal void RemoveAllWindows(){}
        }
    }
    internal static class PortraitManagedDrain
    {
        internal static Task Combine(Action<Exception> error,params Task[] drains)=>Task.WhenAll(drains);
    }
}

