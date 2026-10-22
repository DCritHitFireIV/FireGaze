using System.Reflection;
using FireGaze.LocalPreview.Runtime;

internal static partial class Program
{
    private delegate void OwnedCallback();
    private sealed class CallbackOwner { internal void Callback(){} }
    private static IEnumerable<(string Name,Action Run)> DetourCases()
    {
        yield return("exact instance detour and plugin actor assemblies match",()=>{
            var owner=new CallbackOwner(); OwnedCallback callback=owner.Callback;
            Check(ReviewedDetourContract.OwnerMatches(owner,callback,typeof(Program).Assembly),"bound callback with exact assembly must qualify");
        });
        yield return("foreign plugin assembly rejected",()=>{
            var owner=new CallbackOwner(); OwnedCallback callback=owner.Callback;
            Check(!ReviewedDetourContract.OwnerMatches(owner,callback,typeof(object).Assembly),"plugin scope cannot point at another actor assembly");
        });
        yield return("different instance callback rejected before constructor",()=>{
            var owner=new CallbackOwner(); OwnedCallback callback=new CallbackOwner().Callback;
            Check(!ReviewedDetourContract.OwnerMatches(owner,callback,typeof(Program).Assembly),"runtime owner must be actual callback target");
        });
        yield return("multicast callback rejected before constructor",()=>{
            var owner=new CallbackOwner(); OwnedCallback callback=owner.Callback; callback+=owner.Callback;
            Check(!ReviewedDetourContract.OwnerMatches(owner,callback,typeof(Program).Assembly),"native hook cannot accept multicast detours");
        });
        foreach(var(tuple,want)in new[]{((result:(object?)true,errors:(object?)Array.Empty<Exception>()),true),
            ((result:(object?)false,errors:(object?)Array.Empty<Exception>()),false),
            ((result:(object?)true,errors:(object?)new[]{new Exception()}),false),
            ((result:(object?)true,errors:(object?)null),false),
            ((result:(object?)null,errors:(object?)Array.Empty<Exception>()),false)})
        {
            var captured=tuple;var expected=want;
            yield return($"verifier result requires true and empty error array: {tuple.result}/{(tuple.errors as Array)?.Length.ToString()??"null"}",()=>{
                Check(ReviewedDetourContract.VerificationPassed(captured.result,captured.errors)==expected,"boolean and output errors are both mandatory");
            });
        }
        yield return("actual compiled native delegates retain exact audited ABI",()=>{
            var assembly=typeof(ActorPreviewRuntime).Assembly;
            var actor=assembly.GetType("FireGaze.LocalPreview.Runtime.ActorPreviewRuntime",true)!;
            foreach(var name in new[]{"PlayResolvedTimelineNative","PlayResolvedActionTimelineNative"})
                Check(ReviewedDetourContract.AbiMatches(actor,actor.GetNestedType(name,BindingFlags.NonPublic)!),"current reviewed bool/U1 native delegate must qualify");
            Check(!ReviewedDetourContract.AbiMatches(actor,typeof(Action)),"unrelated callable cannot be admitted by matching only name or size");
        });
    }
}
