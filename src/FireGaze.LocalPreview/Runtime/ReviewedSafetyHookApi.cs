using System.Reflection;
using Dalamud.Hooking;

namespace FireGaze.LocalPreview.Runtime;

internal sealed record ReviewedBootEvidence(byte[] Image,Func<int,int,byte[]?> ReadLoaded);

internal sealed class ReviewedSafetyHookApi<T> : IReviewedDisabledHookApi<Hook<T>> where T:Delegate
{
    private readonly object provider, owner, tracked;
    private readonly T detour;
    private readonly nint address;
    private readonly Func<bool> confirm;
    private readonly Func<ReviewedBootEvidence> readBoot;
    private readonly ReviewedSafetyHookSchema.Members schema;
    private readonly MethodInfo verifier;
    private readonly ConstructorInfo constructor;

    private ReviewedSafetyHookApi(object provider,object owner,T detour,nint address,Func<bool> confirm,
        Func<ReviewedBootEvidence> readBoot,ReviewedSafetyHookSchema.Members schema)
    {
        this.provider=provider;this.owner=owner;this.detour=detour;this.address=address;
        this.confirm=confirm;this.readBoot=readBoot;this.schema=schema;
        // Every reflected member and generic construction is resolved before a
        // native constructor can be entered. No global setting is modified.
        verifier=schema.Verify.MakeGenericMethod(typeof(T));
        constructor=schema.SafetyDefinition.MakeGenericType(typeof(T)).GetConstructors(ReviewedSafetyHookSchema.Declared).Single();
        if(!constructor.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(nint),typeof(T),typeof(Assembly)}))
            throw new InvalidOperationException("Closed official constructor signature changed.");
        tracked=schema.Tracked.GetValue(provider)??throw new InvalidOperationException("Scoped official hook tracking unavailable.");
    }
    internal static bool TryBind(object provider,object owner,T detour,nint address,Func<bool> confirm,
        Func<ReviewedBootEvidence> readBoot,out ReviewedSafetyHookApi<T>? api,out string reason)
    {
        api=null;reason="Reviewed local disabled factory did not qualify.";
        try
        {
            var dalamud=typeof(Hook<T>).Assembly;
            if(!ReviewedSafetyHookSchema.MatchesIdentity(File.ReadAllBytes(dalamud.Location),dalamud.ManifestModule.ModuleVersionId)
                ||!ReviewedSafetyHookSchema.TryRead(dalamud,out var schema)||provider.GetType()!=schema!.Provider
                ||address<=0||confirm is null||readBoot is null)return false;
            var candidate=new ReviewedSafetyHookApi<T>(provider,owner,detour,address,confirm,readBoot,schema);
            candidate.Qualify(owner,detour);
            api=candidate;reason="";return true;
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {reason="Reviewed local disabled factory unavailable: "+error.GetType().Name+": "+error.Message;return false;}
    }
    public void Qualify(object owner,Delegate detour)
    {
        if(!ReferenceEquals(owner,this.owner)||!ReferenceEquals(detour,this.detour)||provider.GetType()!=schema.Provider
            ||!ReferenceEquals(typeof(Hook<T>).Assembly,schema.Assembly)
            ||!ReviewedSafetyHookSchema.MatchesIdentity(File.ReadAllBytes(schema.Assembly.Location),schema.Assembly.ManifestModule.ModuleVersionId))
            throw new InvalidOperationException("Official loaded identity or factory caller changed.");
        var plugin=schema.Plugin.GetValue(provider)??throw new InvalidOperationException("Official plugin scope unavailable.");
        var pluginAssembly=schema.PluginAssembly.GetValue(plugin) as Assembly;
        if(pluginAssembly is null||!ReviewedDetourContract.OwnerMatches(owner,detour,pluginAssembly)
            ||!ReviewedDetourContract.AbiMatches(owner.GetType(),typeof(T))
            ||!ReferenceEquals(schema.Tracked.GetValue(provider),tracked))
            throw new InvalidOperationException("Official plugin, actor, detour ABI or tracking scope mismatch.");
        var boot=readBoot();
        if(!NativeTimelineBackendContract.Matches(true,boot.Image,boot.ReadLoaded)||!confirm())
            throw new InvalidOperationException("Reviewed Boot disabled implementation or native target changed.");
    }
    public void Verify(nint address,Assembly actorAssembly)
    {
        if(address!=this.address||!ReferenceEquals(actorAssembly,owner.GetType().Assembly)||!confirm())
            throw new InvalidOperationException("Reviewed official verifier target changed.");
        object?[] arguments=[address,actorAssembly,null];
        var result=verifier.Invoke(null,arguments);
        if(!ReviewedDetourContract.VerificationPassed(result,arguments[2]))
            throw new InvalidOperationException("Official HookVerifier did not return true with an empty error array.");
    }
    public nint FollowJmp(nint address)
    {
        if(address!=this.address)throw new InvalidOperationException("Unreviewed FollowJmp target.");
        return (nint)schema.Follow.Invoke(null,[address])!;
    }
    public Hook<T> Construct(nint address,Delegate detour,Assembly actorAssembly)
    {
        if(address!=this.address||!ReferenceEquals(detour,this.detour)||!ReferenceEquals(actorAssembly,owner.GetType().Assembly))
            throw new InvalidOperationException("Unreviewed official constructor arguments.");
        Qualify(owner,detour);
        // The official constructor is a trusted boundary. If it throws after
        // native Create, its private partial handle cannot be recovered here.
        // Never treat that failure as a successful hook or automatically retry.
        return (Hook<T>)constructor.Invoke([address,detour,actorAssembly]);
    }
    public bool IsEnabled(Hook<T> hook)=>hook.IsEnabled;
    public Delegate Original(Hook<T> hook)=>hook.Original;
    public void TrackScoped(Hook<T> hook)=>schema.Add.Invoke(tracked,[hook]);
}
