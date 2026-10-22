using System.Reflection;
using System.Runtime.InteropServices;

namespace FireGaze.LocalPreview.Runtime;

internal static class ReviewedDetourContract
{
    internal static bool OwnerMatches(object owner,Delegate detour,Assembly pluginAssembly)
        => ReferenceEquals(detour.Target,owner) && detour.GetInvocationList().Length==1
            && ReferenceEquals(owner.GetType().Assembly,pluginAssembly)
            && ReferenceEquals(detour.GetType().Assembly,pluginAssembly)
            && ReferenceEquals(detour.Method.DeclaringType,owner.GetType()) && !detour.Method.IsStatic;
    internal static bool AbiMatches(Type actorType,Type delegateType)
    {
        if(actorType.FullName!="FireGaze.LocalPreview.Runtime.ActorPreviewRuntime"
            ||delegateType.DeclaringType!=actorType||delegateType.Assembly!=actorType.Assembly
            ||delegateType.IsGenericType||delegateType.ContainsGenericParameters||delegateType.BaseType!=typeof(MulticastDelegate)
            ||delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>() is not null)return false;
        var action=delegateType.Name=="PlayResolvedActionTimelineNative";
        if(!action&&delegateType.Name!="PlayResolvedTimelineNative")return false;
        var method=delegateType.GetMethod("Invoke")!;
        if(method.ReturnType!=typeof(bool)||method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value!=UnmanagedType.U1)return false;
        var parameters=method.GetParameters();
        if(parameters.Length!=4||parameters.Any(p=>p.IsOut||p.IsIn||p.GetCustomAttribute<MarshalAsAttribute>() is not null))return false;
        var pointer=parameters[0].ParameterType;
        if(!pointer.IsPointer||pointer.GetElementType()?.FullName!="FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer"
            ||pointer.GetElementType()?.Assembly.GetName().Name!="FFXIVClientStructs")return false;
        static bool VoidPointer(Type type)=>type.IsPointer&&type.GetElementType()==typeof(void);
        return parameters[1].ParameterType==typeof(ushort)&&VoidPointer(parameters[3].ParameterType)
            &&(action?parameters[2].ParameterType==typeof(ushort):VoidPointer(parameters[2].ParameterType));
    }
    internal static bool VerificationPassed(object? result,object? errors)
        => result is true && errors is Array array && array.Rank==1 && array.Length==0;
}
