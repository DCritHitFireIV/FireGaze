using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

// Boundary doubles prevent all game/native execution. Tests still transform/patch actual HMS IL.
namespace FireGaze.LocalPreview.Runtime {
    public enum PreviewSlot { Mount,Minion,Accessory,Emote }
    public sealed class ActorPreviewRuntime {
        public event Action<ushort>? ExternalEmoteAccepted;
        public bool HasActivePreview {get;set;}
        public int EvidenceCompleted,Rejections,Accepted,Cleanups;
        public CaptureProjection GetCaptureProjection()=>new(1,false,0,0,0,null);
        public long BeginExternalPresentation(int kind,uint id)=>HasActivePreview?1:0;
        public bool EndExternalPresentation(long token,bool accepted) {if(accepted) Accepted++; else Rejections++;return true;}
        public long BeginHmsEmoteEvidence(ushort id)=>HasActivePreview?2:0;
        public void CompleteHmsEmoteEvidence(long token,bool accepted)=>EvidenceCompleted++;
        public void BeforeHmsLifecycle()=>Cleanups++;
        public void Observe(ushort id)=>ExternalEmoteAccepted?.Invoke(id);
    }
}
namespace FireGaze.LocalPreview.Compatibility {
    public sealed class HmsShadowActor:IDisposable {
        public static bool BeginAllowed=true;
        public HmsShadowActor(Runtime.ActorPreviewRuntime runtime,IPluginLog log) {}
        public bool TryBeginCapture(out IDisposable? scope) {scope=null;return BeginAllowed;}
        public nint ReadDetectorAddress(IGameObject obj)=>throw new InvalidOperationException("Native access forbidden in offline test");
        public nint ReadBodyAddress(IGameObject obj)=>throw new InvalidOperationException("Native access forbidden in offline test");
        public void Dispose(){}
    }
}
public class NullService:DispatchProxy {
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>targetMethod?.ReturnType.IsValueType==true && targetMethod.ReturnType!=typeof(void)?Activator.CreateInstance(targetMethod.ReturnType):null;
    public static T New<T>() where T:class=>Create<T,NullService>();
}
public class ObjectTableService:DispatchProxy {
    public int LocalReads;
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args) {
        if(targetMethod?.Name=="get_LocalPlayer") LocalReads++;
        return targetMethod?.ReturnType.IsValueType==true && targetMethod.ReturnType!=typeof(void)?Activator.CreateInstance(targetMethod.ReturnType):null;
    }
}
