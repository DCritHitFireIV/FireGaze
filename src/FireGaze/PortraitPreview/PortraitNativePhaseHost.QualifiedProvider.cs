namespace FireGaze.PortraitPreview;

// The same audited official provider chain serves phase observation and owned
// forwarding. A name returned by an arbitrary factory is never qualification.
internal interface IPortraitQualifiedNativeHookProvider
{
    string Identity { get; }
    bool TryQualifyStartDisabled(out string reason);
    bool QualificationPreserved();
    Delegate DirectTarget(nint address, Type delegateType);
    IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type delegateType, Delegate detour, Func<bool> verifyEntry);
}

internal interface IPortraitNativeHookInspection
{
    // The object is the actual qualified provider's returned Hook<T>. Inspection
    // is synchronous, read-only, and never performed while holding a gateway gate.
    bool TryInspect(Func<object, bool> inspect);
}
