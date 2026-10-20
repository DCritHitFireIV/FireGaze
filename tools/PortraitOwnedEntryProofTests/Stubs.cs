namespace FireGaze.PortraitPreview;
internal enum PortraitOwnedSceneNativeKind { Dtor, Release, Initialize, Update, Setup, Render, CreateClientObject, DeleteClientObject, ConfigureContext, ConfigureDefaultContext, CleanupContext, ConstructContext, DestroyContext }
internal readonly record struct PortraitOwnedSceneNativeTarget(PortraitOwnedSceneNativeKind Kind, nint Address);
internal interface IPortraitOwnedSceneTransportProof { bool TryValidate(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason); bool VerifyEntry(PortraitOwnedSceneNativeTarget target); }
internal interface IPortraitNativeHookInspection { bool TryInspect(Func<object,bool> inspect); }
internal interface IPortraitNativePhaseHostHook : IDisposable { nint Address {get;} string BackendName {get;} bool IsEnabled {get;} bool IsDisposed {get;} Delegate Original {get;} void Enable(); void Disable(); }
internal interface IPortraitOwnedSceneCreatedHookProof { bool VerifyCreatedHook(PortraitOwnedSceneNativeTarget target, IPortraitNativePhaseHostHook hook, Delegate stableDetour, out string reason); }
internal static class PortraitOwnedSceneHooks { public static bool NativeAbiMatchesSDK(out string reason) { reason=""; return true; } }
internal static class PortraitNativeBootImageQualifier { public static bool TryQualifyCurrentProcess(out object? identity, out string reason) { identity=null; reason="No native backend in offline tests"; return false; } }
