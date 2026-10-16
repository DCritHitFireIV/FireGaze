using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;
using HarmonyLib;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>
/// Optional in-memory adapter for audited, unmodified HMS binaries. All original operations run
/// once; capture reads see an owned shadow, while native inputs keep their genuine return values.
/// This does not install an HMS replacement, enter a session, or change its map/movement logic.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HmsOriginalAdapter : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly ActorPreviewRuntime runtime;
    private readonly IPluginLog log;
    private readonly HmsShadowActor shadow;
    private readonly Harmony harmony = new("FireGaze.LocalPreview.HmsOriginal." + Guid.NewGuid().ToString("N"));
    private readonly List<MethodInfo> patched = new();
    private Assembly? assembly;
    private object? plugin;
    private object? zoneLoad;
    private object? detector;
    private object? capture;
    private object? apply;
    private WeakReference<Assembly>? retired;
    private WeakReference<Assembly>? rejected;
    private string? fault;
    private bool disposed;
    private static HmsOriginalAdapter? current;
    [ThreadStatic] private static Stack<InputCall>? inputCalls;
    [ThreadStatic] private static Stack<CaptureCall>? captureCalls;

    public HmsOriginalAdapter(IDalamudPluginInterface pluginInterface, ICommandManager commands,
        ActorPreviewRuntime runtime, IPluginLog log)
    {
        this.pluginInterface=pluginInterface;
        this.commands=commands;
        this.runtime=runtime;
        this.log=log;
        shadow=new(runtime,log);
        runtime.ExternalEmoteAccepted+=ObserveNativeEmote;
    }

    public HmsCoexistenceDecision Refresh()
    {
        if(disposed) return new(false,"HMS 兼容适配器已卸载。");
        try
        {
            var loaded=pluginInterface.InstalledPlugins.Where(p=>p.IsLoaded &&
                (p.InternalName is "HMSync" or "HMSyncDev" || p.Name.Contains("HMapSync",StringComparison.OrdinalIgnoreCase))).ToArray();
            if(loaded.Length==0) { Detach(); return new(true,""); }
            if(loaded.Length!=1) { Detach(); return new(false,"检测到多个 HMS 同时加载，请仅加载一个后再试。"); }
            object? instance=FindInstance(loaded[0]);
            if(instance==null && commands.Commands.TryGetValue("/hms",out var command))
                instance=command.Handler.Target?.GetType().FullName=="HMSync.HMSyncPlugin" ? command.Handler.Target : null;
            if(instance==null || instance.GetType().FullName!="HMSync.HMSyncPlugin")
            { Detach(); return new(false,"HMS 正在加载或其插件实例暂不可读取，请稍后再试。"); }
            var candidate=instance.GetType().Assembly;
            if(rejected!=null && rejected.TryGetTarget(out var refused) && ReferenceEquals(refused,candidate))
                return new(false,fault??"该 HMS 版本尚未通过本地试用兼容验证。");
            if(retired!=null && retired.TryGetTarget(out var old) && ReferenceEquals(old,candidate))
                return new(false,"HMS 正在卸载，请等待重载完成。");
            if(!ReferenceEquals(candidate,assembly) || !ReferenceEquals(instance,plugin))
            {
                Detach();
                try { Install(candidate,instance,loaded[0]); rejected=null; }
                catch { rejected=new(candidate); throw; }
            }
            if(fault!=null) return new(false,fault);
            if(patched.Count==0 || patched.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Contains(harmony.Id)!=true))
                throw new InvalidOperationException("HMS compatibility patches are incomplete or were removed");
            if(ReadBool(zoneLoad,"IsTransitioning") || ReadBool(zoneLoad,"HomeRestoreArmed"))
            { runtime.BeforeHmsLifecycle(); return new(false,"HMS 正在切换或恢复场景，请稍后再试。"); }
            return new(true,"");
        }
        catch(Exception ex)
        {
            Fail(ex);
            return new(false,fault!);
        }
    }

    private void Install(Assembly target, object instance, object exposed)
    {
        if(current!=null && !ReferenceEquals(current,this)) throw new InvalidOperationException("Another local preview host already owns HMS compatibility");
        // Dalamud may load plugin assemblies from streams; Assembly.Location is then
        // empty. Read only the DLL belonging to this exact exposed LocalPlugin instance.
        var local=FindLocalPlugin(exposed);
        if(local==null || !ReferenceEquals(ReadField(local,"instance"),instance)
            || !ReferenceEquals(instance.GetType().Assembly,target))
            throw new InvalidOperationException("HMS exposed plugin instance differs from the loaded candidate");
        var file=local.GetType().GetProperty("DllFile",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(local) as FileInfo
            ?? throw new InvalidOperationException("HMS exposed plugin DLL file is unavailable");
        var bytes=File.ReadAllBytes(file.FullName);
        if(!ReferenceEquals(ReadField(local,"instance"),instance))
            throw new InvalidOperationException("HMS exposed plugin instance changed during binary verification");
        var profile=HmsOriginalFingerprint.Identify(bytes) ?? throw new InvalidOperationException("Unsupported HMS binary fingerprint");
        if(target.ManifestModule.ModuleVersionId!=profile.Mvid || target.GetName().Version!=profile.Version)
            throw new InvalidOperationException("Loaded HMS identity differs from the audited file");
        var root=target.GetType("HMSync.HMSyncPlugin",true)!;
        var detectorType=target.GetType("HMSync.Services.LocalStateDetector",true)!;
        var captureType=target.GetType("HMSync.Services.StateCaptureService",true)!;
        var applyType=target.GetType("HMSync.Services.StateApplyService",true)!;
        var zoneType=target.GetType("HMSync.Services.ZoneLoadService",true)!;
        var visibilityType=target.GetType("HMSync.Services.ActorVisibilityService",true)!;
        object Field(string name,Type expected)
        {
            var field=root.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            if(field?.FieldType!=expected || field.GetValue(instance) is not { } value) throw new InvalidOperationException("HMS field changed: "+name);
            return value;
        }
        var ownDetector=Field("detector",detectorType);
        var ownCapture=Field("stateCapture",captureType);
        var ownApply=Field("stateApply",applyType);
        var zone=Field("zoneLoad",zoneType);
        foreach(var property in new[]{"IsTransitioning","HomeRestoreArmed"})
            if(zoneType.GetProperty(property)?.PropertyType!=typeof(bool)) throw new InvalidOperationException("HMS lifecycle property changed: "+property);

        MethodInfo Method(Type type,string name,string returns,params string[] parameters)
        {
            var matches=type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name==name).ToArray();
            if(matches.Length!=1) throw new InvalidOperationException("HMS method changed: "+name);
            var result=matches[0];
            if(result.IsGenericMethod || TypeName(result.ReturnType)!=returns || !result.GetParameters().Select(p=>TypeName(p.ParameterType)).SequenceEqual(parameters))
                throw new InvalidOperationException("HMS signature changed: "+name);
            HmsOriginalFingerprint.VerifyLoadedMethod(bytes,result);
            if(Harmony.GetPatchInfo(result)?.Owners.Any()==true) throw new InvalidOperationException("HMS method already patched by another owner: "+name);
            return result;
        }
        var detect=Method(detectorType,"Detect","System.Nullable`1<HMSync.Services.LocalActorState>");
        var reset=Method(detectorType,"Reset","System.Void");
        var capture=Method(captureType,"OnFrameworkUpdate","System.Void","Dalamud.Plugin.Services.IFramework");
        var mount=Method(applyType,"MountSelf","HMSync.Services.StateApplyService+MountResult","System.Int16");
        var minion=Method(applyType,"SummonMinionSelf","System.Boolean","System.Int16");
        var ornament=Method(applyType,"SummonOrnamentSelf","System.Boolean","System.Int16");
        var emote=Method(root,"PlayResolvedEmoteCore","System.Void","System.UInt16","System.UInt16","System.UInt16","System.String");
        var stop=Method(root,"StopEmoteSelf","System.Void");
        foreach(var name in new[]{"CurrentMountId","CurrentMinionId","CurrentOrnamentId"}) Method(root,name,"System.UInt16");
        var semantics=new[]{Method(root,"OnCommand","System.Void","System.String","System.String"),Method(root,"DoMinion","System.Void","System.String"),Method(root,"DoAccessory","System.Void","System.String")};
        var lifecycle=new[]{Method(root,"EngageSyntheticSession","System.Boolean"),Method(root,"DoLoad","System.Void","System.UInt32"),
            Method(root,"DoReload","System.Void"),Method(root,"DoLeaveInternal","System.Void","System.Boolean"),
            Method(zoneType,"LoadZone","System.Void","System.UInt32","System.Collections.Generic.HashSet`1<System.UInt16>","System.Nullable`1<FFXIVClientStructs.FFXIV.Common.Math.Vector3>","System.Nullable`1<System.Single>"),
            Method(zoneType,"ReloadZone","System.Void","System.Collections.Generic.HashSet`1<System.UInt16>")};
        var dispose=Method(root,"Dispose","System.Void");
        var visibility=new[]{Method(visibilityType,"HideAll","System.Void"),Method(visibilityType,"Update","System.Void")};
        // Validate every transformation before installing any hook.
        HmsHarmonyIsolation.ValidateRuntime();
        foreach(var method in new[]{detect,reset,capture,mount,minion,ornament,emote,stop,dispose}.Concat(semantics).Concat(lifecycle).Concat(visibility))
            HmsHarmonyIsolation.PreserveGenericMembers(PatchProcessor.GetOriginalInstructions(method),method).ToArray();
        foreach(var method in new[]{detect,reset}) HmsOriginalPatchRules.ReplaceCaptureAddresses(PatchProcessor.GetOriginalInstructions(method),Bridge(nameof(DetectorAddress)),true).ToArray();
        HmsOriginalPatchRules.ReplaceCaptureAddresses(PatchProcessor.GetOriginalInstructions(capture),Bridge(nameof(BodyAddress)),false).ToArray();
        HmsOriginalPatchRules.MarkEmoteDispatches(PatchProcessor.GetOriginalInstructions(emote),Bridge(nameof(MarkDispatch))).ToArray();
        HmsOriginalPatchRules.MarkEmoteStop(PatchProcessor.GetOriginalInstructions(stop),Bridge(nameof(MarkDispatch))).ToArray();
        foreach(var method in new[]{mount,minion,ornament}) HmsOriginalPatchRules.MarkPresentationDispatches(PatchProcessor.GetOriginalInstructions(method),Bridge(nameof(MarkDispatch)),method.Name).ToArray();
        foreach(var method in semantics) HmsOriginalPatchRules.ReplaceSemanticReads(PatchProcessor.GetOriginalInstructions(method),Bridge(SemanticBridge(method.Name)),method.Name).ToArray();
        foreach(var method in visibility) HmsOriginalPatchRules.ReplaceVisibilityKindReads(PatchProcessor.GetOriginalInstructions(method),Bridge(nameof(ReadVisibilityKind)),method.Name=="HideAll"?1:2).ToArray();

        runtime.BeforeHmsLifecycle();
        assembly=target; plugin=instance; zoneLoad=zone; detector=ownDetector; this.capture=ownCapture; apply=ownApply; current=this; fault=null;
        try
        {
            foreach(var method in new[]{detect,reset,capture}) Patch(method,nameof(CapturePrefix),null,nameof(CaptureTranspiler),nameof(CaptureFinalizer));
            foreach(var method in new[]{mount,minion,ornament}) Patch(method,nameof(InputPrefix),null,nameof(SetupTranspiler),nameof(InputFinalizer));
            Patch(emote,nameof(EmotePrefix),null,nameof(EmoteTranspiler),nameof(InputFinalizer));
            Patch(stop,nameof(StopPrefix),null,nameof(StopTranspiler),nameof(InputFinalizer));
            // Transform the actual comparisons rather than small getters that may already be JIT-inlined.
            foreach(var method in semantics) Patch(method,null,null,nameof(SemanticTranspiler));
            foreach(var method in visibility) Patch(method,null,null,nameof(VisibilityTranspiler));
            foreach(var method in lifecycle) Patch(method,nameof(LifecyclePrefix));
            Patch(dispose,nameof(DisposePrefix));
            log.Information("[LocalPreview] Original HMS adapter installed: {Profile}, {Mvid}",profile.Name,profile.Mvid);
        }
        catch { Detach(); throw; }
    }

    private static string TypeName(Type type) => type.IsGenericType
        ? type.GetGenericTypeDefinition().FullName+"<"+string.Join(",",type.GetGenericArguments().Select(TypeName))+">" : type.FullName!;
    private static MethodInfo Bridge(string name)=>typeof(HmsOriginalAdapter).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!;
    private static HarmonyMethod? PatchMethod(string? name)=>name==null?null:new HarmonyMethod(Bridge(name));
    private void Patch(MethodInfo method,string? prefix=null,string? postfix=null,string? transpiler=null,string? finalizer=null)
    {
        // Harmony can register its detour before a later callback fails. Track the attempted
        // target first so Install's cleanup also sees that partially completed operation.
        patched.Add(method);
        harmony.Patch(method,PatchMethod(prefix),PatchMethod(postfix),PatchMethod(transpiler??nameof(GenericTranspiler)),PatchMethod(finalizer));
    }

    private static bool CapturePrefix(object __instance,MethodBase __originalMethod,out CaptureCall __state)
    {
        var owner=current;
        bool owned=owner!=null && (ReferenceEquals(__instance,owner.detector) || ReferenceEquals(__instance,owner.capture));
        __state=new(owned?owner:null);
        (captureCalls??=new()).Push(__state);
        if(!owned) return true;
        try
        {
            if(owner!.fault!=null && owner.runtime.HasActivePreview) owner.runtime.BeforeHmsLifecycle();
            // Without trial ownership, leave HMS's original capture completely untouched. This
            // also keeps original HMS working after an unsupported native layout disables trials.
            if(!owner.runtime.HasActivePreview) { __state.Owner=null; return true; }
            if(owner.shadow.TryBeginCapture(out var scope)) { __state.Scope=scope; return true; }
            owner.Fail(new InvalidOperationException("HMS capture shadow refused the current preview"));
            // Cleanup removed the trial, so original Reset/Detect/capture can safely proceed.
            // If cleanup cannot remove it, skip this sample rather than expose a trial pointer.
            if(!owner.runtime.HasActivePreview) { __state.Owner=null; return true; }
            return false;
        }
        catch(Exception ex) { owner!.Fail(ex); return false; }
    }
    private static Exception? CaptureFinalizer(Exception? __exception,CaptureCall? __state)
    {
        try
        {
            __state?.Scope?.Dispose();
            if(captureCalls?.TryPeek(out var head)==true && ReferenceEquals(head,__state)) captureCalls.Pop();
        }
        catch(Exception ex) { __state?.Owner?.Fail(ex); }
        if(__exception is HmsCaptureIsolationException && __state?.Owner is { } owner) { owner.Fail(__exception); return null; }
        return __exception;
    }
    private static nint DetectorAddress(IGameObject value)
    {
        try { return captureCalls?.TryPeek(out var call)==true && call.Owner is { } owner ? owner.shadow.ReadDetectorAddress(value) : value.Address; }
        catch(Exception ex) { throw new HmsCaptureIsolationException(ex); }
    }
    private static nint BodyAddress(IGameObject value)
    {
        try { return captureCalls?.TryPeek(out var call)==true && call.Owner is { } owner ? owner.shadow.ReadBodyAddress(value) : value.Address; }
        catch(Exception ex) { throw new HmsCaptureIsolationException(ex); }
    }
    private static IEnumerable<CodeInstruction> CaptureTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        => HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.ReplaceCaptureAddresses(instructions,Bridge(__originalMethod.Name=="OnFrameworkUpdate"?nameof(BodyAddress):nameof(DetectorAddress)),__originalMethod.Name!="OnFrameworkUpdate"),__originalMethod);
    private static IEnumerable<CodeInstruction> GenericTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(instructions,__originalMethod);
    private static IEnumerable<CodeInstruction> EmoteTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.MarkEmoteDispatches(instructions,Bridge(nameof(MarkDispatch))),__originalMethod);
    private static IEnumerable<CodeInstruction> StopTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.MarkEmoteStop(instructions,Bridge(nameof(MarkDispatch))),__originalMethod);
    private static IEnumerable<CodeInstruction> SetupTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.MarkPresentationDispatches(instructions,Bridge(nameof(MarkDispatch)),__originalMethod.Name),__originalMethod);
    private static string SemanticBridge(string method)=>method switch { "OnCommand"=>nameof(ReadMount),"DoMinion"=>nameof(ReadMinion),_=>nameof(ReadOrnament) };
    private static IEnumerable<CodeInstruction> SemanticTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.ReplaceSemanticReads(instructions,Bridge(SemanticBridge(__originalMethod.Name)),__originalMethod.Name),__originalMethod);
    private static IEnumerable<CodeInstruction> VisibilityTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)=>HmsHarmonyIsolation.PreserveGenericMembers(HmsOriginalPatchRules.ReplaceVisibilityKindReads(instructions,Bridge(nameof(ReadVisibilityKind)),__originalMethod.Name=="HideAll"?1:2),__originalMethod);
    private static unsafe FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind ReadVisibilityKind(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* actor)
    {
        // Exempt only this allocation from HMS's non-peer player hiding. No actual kind,
        // HMS peer roster, materialization watch or gameplay identity is changed.
        if(current?.runtime.OwnsDisplayActor((nint)actor)==true) return FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc;
        return actor->ObjectKind;
    }

    private static void InputPrefix(object __instance,MethodBase __originalMethod,object[] __args,out InputCall? __state)
    {
        var kind=__originalMethod.Name switch { "MountSelf"=>PreviewSlot.Mount,"SummonMinionSelf"=>PreviewSlot.Minion,_=>PreviewSlot.Accessory };
        short id=(short)__args[0];
        __state=current?.Begin(kind,id>0?(ushort)id:(ushort)0,false,ReferenceEquals(__instance,current.apply));
    }
    private static void EmotePrefix(object __instance,ushort emoteId,out InputCall? __state)=>__state=current?.Begin(PreviewSlot.Emote,emoteId,true,ReferenceEquals(__instance,current.plugin));
    private static void StopPrefix(object __instance,out InputCall? __state)=>__state=current?.Begin(PreviewSlot.Emote,0,true,ReferenceEquals(__instance,current.plugin));
    private InputCall? Begin(PreviewSlot kind,ushort id,bool evidence,bool owned)
    {
        if(disposed || fault!=null) return null;
        var state=new InputCall(this,kind,id,owned);
        try
        {
            if(owned) {
                state.ExternalToken=runtime.BeginExternalPresentation((int)kind,id);
                if(evidence) state.EvidenceToken=runtime.BeginHmsEmoteEvidence(id);
            }
            (inputCalls??=new()).Push(state);
            return state;
        }
        catch(Exception ex)
        {
            if(state.ExternalToken!=0) runtime.EndExternalPresentation(state.ExternalToken,false);
            Fail(ex);
            return null;
        }
    }
    private static void MarkDispatch() { if(inputCalls?.TryPeek(out var state)==true && state.Owned) state.Accepted=true; }
    private void ObserveNativeEmote(ushort id)
    {
        if(inputCalls==null) return;
        foreach(var state in inputCalls)
            if(ReferenceEquals(state.Owner,this) && state.Owned && state.Kind==PreviewSlot.Emote && state.Id==id) { state.Accepted=true; break; }
    }
    private static Exception? InputFinalizer(Exception? __exception,InputCall? __state)
    {
        if(__state==null) return __exception;
        var owner=__state.Owner;
        try
        {
            if(inputCalls?.TryPeek(out var head)==true && ReferenceEquals(head,__state)) inputCalls.Pop();
            else throw new InvalidOperationException("HMS presentation call stack changed");
            // A successful dispatch remains accepted if a later HMS log callback throws.
            if(__state.EvidenceToken!=0) owner.runtime.CompleteHmsEmoteEvidence(__state.EvidenceToken,__state.Accepted);
            if(__state.ExternalToken!=0) owner.runtime.EndExternalPresentation(__state.ExternalToken,__state.Accepted);
        }
        catch(Exception ex) { owner.Fail(ex); }
        return __exception;
    }
    private static ushort ReadMount(object instance)=>ReadSemanticId(instance,"CurrentMountId");
    private static ushort ReadMinion(object instance)=>ReadSemanticId(instance,"CurrentMinionId");
    private static ushort ReadOrnament(object instance)=>ReadSemanticId(instance,"CurrentOrnamentId");
    private static ushort ReadSemanticId(object instance,string getter)
    {
        var projection=ReferenceEquals(instance,current?.plugin)?current.runtime.GetCaptureProjection():null;
        if(projection is { Active:true,Baseline: { } baseline })
            return getter switch { "CurrentMountId"=>baseline.MountId,"CurrentMinionId"=>baseline.MinionId,_=>baseline.OrnamentId };
        var original=instance.GetType().GetMethod(getter,BindingFlags.Instance|BindingFlags.NonPublic)!;
        try { return (ushort)original.Invoke(instance,null)!; }
        catch(TargetInvocationException ex) when(ex.InnerException!=null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void LifecyclePrefix(object __instance)
    {
        var owner=current;
        if(owner==null || !(ReferenceEquals(__instance,owner.plugin) || ReferenceEquals(__instance,owner.zoneLoad))) return;
        try { owner.runtime.BeforeHmsLifecycle(); } catch(Exception ex) { owner.Fail(ex); }
    }
    private static void DisposePrefix(object __instance)
    {
        var owner=current;
        if(owner==null || !ReferenceEquals(__instance,owner.plugin)) return;
        if(owner.assembly!=null) owner.retired=new(owner.assembly);
        owner.Detach();
    }
    private void Fail(Exception ex)
    {
        if(fault==null)
        {
            fault="当前 HMS 版本或运行时接口尚未通过兼容验证，已关闭本地试用；HMS 可继续使用。";
            try { log.Error(ex,"[LocalPreview] Original HMS compatibility disabled; original operations are never replayed."); } catch { }
        }
        try { runtime.BeforeHmsLifecycle(); } catch { }
    }
    private void Detach()
    {
        if(assembly==null && patched.Count==0) return;
        if(assembly!=null) { try { runtime.BeforeHmsLifecycle(); } catch(Exception ex) { log.Error(ex,"[LocalPreview] HMS detach cleanup failed"); } }
        try { HmsHarmonyIsolation.Detach(harmony,patched); }
        catch(Exception ex) { try { log.Error(ex,"[LocalPreview] HMS patch detach failed; original HMS disposal will continue"); } catch { } }
        finally { patched.Clear(); if(ReferenceEquals(current,this)) current=null; assembly=null; plugin=null; zoneLoad=null; detector=null;capture=null;apply=null; fault=null; }
    }
    private static bool ReadBool(object? value,string name)=>value?.GetType().GetProperty(name)?.GetValue(value) as bool? ?? false;
    private static object? FindInstance(object exposed)
    {
        // Current Dalamud captures this primary-constructor argument as <plugin>P; older
        // builds used an explicit plugin field. Both still expose the same exact LocalPlugin.
        object? raw=FindLocalPlugin(exposed);
        return raw==null?null:ReadField(raw,"instance");
    }
    private static object? FindLocalPlugin(object exposed)=>ReadField(exposed,"plugin")??ReadField(exposed,"<plugin>P");
    private static object? ReadField(object value,string name)
    {
        for(var type=value.GetType();type!=null;type=type.BaseType)
            if(type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly) is { } field) return field.GetValue(value);
        return null;
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true;
        runtime.ExternalEmoteAccepted-=ObserveNativeEmote;
        Detach();
        shadow.Dispose();
    }
    private sealed class InputCall(HmsOriginalAdapter owner,PreviewSlot kind,ushort id,bool owned)
    {
        public HmsOriginalAdapter Owner { get; }=owner;
        public PreviewSlot Kind { get; }=kind;
        public ushort Id { get; }=id;
        public bool Owned { get; }=owned;
        public long ExternalToken;
        public long EvidenceToken;
        public bool Accepted;
    }
    private sealed class CaptureCall(HmsOriginalAdapter? owner)
    { public HmsOriginalAdapter? Owner=owner; public IDisposable? Scope; }
    private sealed class HmsCaptureIsolationException(Exception inner):Exception("HMS local preview capture isolation failed",inner);
}
