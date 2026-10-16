using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using System.Runtime.CompilerServices;
using FireGaze.LocalPreview.Compatibility;
using FireGaze.LocalPreview.Runtime;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
var hooks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
var release = Path.Combine(root, "hms-original-1.0.4.2/unpacked/HMSync.dll");
var dev = Environment.GetEnvironmentVariable("FIREGAZE_HMS_DEV_DLL") ?? Path.Combine(work, "hms-development/HMSync.dll");
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var dir in new[] { hooks, Path.GetDirectoryName(release)! }) {
        var file = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
var tests = new (string, Action)[] {
    ("official release and exact local dev pass binary guard", KnownBinaries),
    ("one changed byte is rejected before a patch can be installed", ChangedBinary),
    ("real capture IL replaces body address and retains live gaze address", CapturePattern),
    ("unexpected address count refuses a partial transformation", ChangedPattern),
    ("real emote IL marks three requested timelines and excludes weapon auxiliaries", EmotePattern),
    ("unexpected emote dispatch shape is rejected", ChangedEmotePattern),
    ("real self setup branches mark dispatch before a later logging exception", SetupPatterns),
    ("real emote stop marks an actual stop only", StopPattern),
    ("all original release wrappers install atomically and unpatch on dispose", InstallRelease),
    ("after trial cancellation a fault never freezes original HMS capture", FaultResumesOriginal),
    ("another detector instance cannot cancel local preview", OtherInstance),
    ("audited local dev wrappers install in a separate plugin load context", InstallDev),
    ("real toggle comparisons project at call sites even when original getters were inlined", SemanticComparisons),
    ("real emote core rejection completes evidence without claiming native execution", CoreRejects),
    ("real Dalamud exposed plugin resolves the exact original HMS instance", DiscoverInstance),
};
int failed = 0;
foreach (var (name, test) in tests) { try { test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
Console.WriteLine($"{tests.Length-failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

Type Rules() => typeof(Program).Assembly.GetType("FireGaze.LocalPreview.Compatibility.HmsOriginalPatchRules") ?? throw new Exception("HMS IL transformation feature is missing");
object? Invoke(string name, params object[] args) => Rules().GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual,expected)) throw new Exception($"expected {expected}; got {actual}"); }
bool Recognized(byte[] bytes) {
    var type = typeof(Program).Assembly.GetType("FireGaze.LocalPreview.Compatibility.HmsOriginalFingerprint") ?? throw new Exception("HMS binary guard feature is missing");
    return type.GetMethod("Identify", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,new object[]{bytes}) != null;
}
void KnownBinaries() { Equal(Recognized(File.ReadAllBytes(release)),true); Equal(Recognized(File.ReadAllBytes(dev)),true); }
void ChangedBinary() { var bytes = File.ReadAllBytes(release); bytes[^1] ^= 0x40; Equal(Recognized(bytes),false); }
List<CodeInstruction> RealIl(string type, string method) {
    var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "HMSync") ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(release);
    var target = assembly.GetType(type,true)!.GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!;
    return PatchProcessor.GetOriginalInstructions(target).ToList();
}
MethodInfo Address() => typeof(Fixture).GetMethod(nameof(Fixture.Address))!;
MethodInfo Marker() => typeof(Fixture).GetMethod(nameof(Fixture.Marker))!;
bool IsAddress(CodeInstruction instruction) => instruction.operand is MethodInfo m && m.Name == "get_Address";
void CapturePattern() {
    var input = RealIl("HMSync.Services.StateCaptureService","OnFrameworkUpdate");
    var output = ((IEnumerable<CodeInstruction>)Invoke("ReplaceCaptureAddresses",input,Address(),false)!).ToList();
    Equal(input.Count(IsAddress),2); Equal(output.Count(IsAddress),1);
    Equal(output.Count(i => Equals(i.operand,Address())),1);
    Equal(output.Any(i => i.opcode == OpCodes.Stind_I1),true);
    var all = output.Where(i => IsAddress(i) || Equals(i.operand,Address())).ToList();
    Equal(Equals(all[0].operand,Address()),true); Equal(IsAddress(all[1]),true);
}
void ChangedPattern() {
    var input = RealIl("HMSync.Services.StateCaptureService","OnFrameworkUpdate");
    input.Add(input.First(IsAddress));
    try { Invoke("ReplaceCaptureAddresses",input,Address(),false); throw new Exception("expected pattern rejection"); }
    catch (TargetInvocationException ex) when(ex.InnerException is InvalidOperationException) { }
}
void EmotePattern() {
    var input = RealIl("HMSync.HMSyncPlugin","PlayResolvedEmoteCore");
    var output = ((IEnumerable<CodeInstruction>)Invoke("MarkEmoteDispatches",input,Marker())!).ToList();
    Equal(output.Count(i=>Equals(i.operand,Marker())),3);
    foreach(var i in Enumerable.Range(0,output.Count).Where(i=>Equals(output[i].operand,Marker()))) {
        Equal(output[i-1].operand is MethodInfo method && method.Name is "PlayTimeline" or "PlayActionTimeline",true);
    }
    var timelineCalls = output.Select((instruction,index)=>(instruction,index)).Where(x=>x.instruction.operand is MethodInfo m && m.Name=="PlayTimeline").ToList();
    Equal(timelineCalls.Count,4);
    Equal(Equals(output[timelineCalls[0].index+1].operand,Marker()),false);
    Equal(Equals(output[timelineCalls[1].index+1].operand,Marker()),false);
}
void ChangedEmotePattern() {
    var input = RealIl("HMSync.HMSyncPlugin","PlayResolvedEmoteCore");
    input.Remove(input.First(i=>i.operand is MethodInfo m && m.Name=="PlayTimeline"));
    try { Invoke("MarkEmoteDispatches",input,Marker()); throw new Exception("expected dispatch rejection"); }
    catch (TargetInvocationException ex) when(ex.InnerException is InvalidOperationException) { }
}
void SetupPatterns() {
    foreach(var method in new[]{"MountSelf","SummonMinionSelf","SummonOrnamentSelf"}) {
        var input=RealIl("HMSync.Services.StateApplyService",method);
        var output=((IEnumerable<CodeInstruction>)Invoke("MarkPresentationDispatches",input,Marker(),method)!).ToList();
        Equal(output.Count(i=>Equals(i.operand,Marker())),2);
        foreach(var i in Enumerable.Range(0,output.Count).Where(i=>Equals(output[i].operand,Marker())))
            Equal(output[i-1].operand is MethodInfo m && m.Name is "CreateAndSetupMount" or "SetupCompanion" or "SetupOrnament",true);
    }
}
void StopPattern() {
    var input=RealIl("HMSync.HMSyncPlugin","StopEmoteSelf");
    var output=((IEnumerable<CodeInstruction>)Invoke("MarkEmoteStop",input,Marker())!).ToList();
    Equal(output.Count(i=>Equals(i.operand,Marker())),1);
}
void InstallRelease() {
    var target=AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="HMSync");
    var type=target.GetType("HMSync.HMSyncPlugin",true)!;
    var instance=RuntimeHelpers.GetUninitializedObject(type);
    foreach(var name in new[]{"detector","stateCapture","stateApply","zoneLoad"}) {
        var field=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
        field.SetValue(instance,RuntimeHelpers.GetUninitializedObject(field.FieldType));
    }
    var runtime=new ActorPreviewRuntime();
    using(var adapter=new HmsOriginalAdapter(NullService.New<IDalamudPluginInterface>(),NullService.New<ICommandManager>(),runtime,NullService.New<IPluginLog>())) {
        typeof(HmsOriginalAdapter).GetMethod("Install",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(adapter,new[]{(object)target,instance});
        var detect=target.GetType("HMSync.Services.LocalStateDetector")!.GetMethod("Detect")!;
        Equal(Harmony.GetPatchInfo(detect)?.Owners.Any(o=>o.StartsWith("FireGaze.LocalPreview.HmsOriginal.",StringComparison.Ordinal)),true);
        // active=false: invoke genuine transformed method safely without reaching native memory.
        var detector=type.GetField("detector",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance);
        Equal(detect.Invoke(detector,null),null);
    }
    var method=target.GetType("HMSync.Services.LocalStateDetector")!.GetMethod("Detect")!;
    Equal(Harmony.GetPatchInfo(method)?.Owners.Any(o=>o.StartsWith("FireGaze.LocalPreview.HmsOriginal.",StringComparison.Ordinal))??false,false);
}
void InInstalled(Action<Assembly,object,ActorPreviewRuntime,HmsOriginalAdapter> test,Assembly? supplied=null) {
    var target=supplied??AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="HMSync");
    var type=target.GetType("HMSync.HMSyncPlugin",true)!;
    var instance=RuntimeHelpers.GetUninitializedObject(type);
    foreach(var name in new[]{"detector","stateCapture","stateApply","zoneLoad"}) {
        var field=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
        field.SetValue(instance,RuntimeHelpers.GetUninitializedObject(field.FieldType));
    }
    var runtime=new ActorPreviewRuntime();
    using var adapter=new HmsOriginalAdapter(NullService.New<IDalamudPluginInterface>(),NullService.New<ICommandManager>(),runtime,NullService.New<IPluginLog>());
    typeof(HmsOriginalAdapter).GetMethod("Install",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(adapter,new[]{(object)target,instance});
    try {test(target,instance,runtime,adapter);} finally {HmsShadowActor.BeginAllowed=true;}
}
void FaultResumesOriginal()=>InInstalled((target,instance,runtime,adapter)=> {
    var root=instance.GetType();
    var detector=root.GetField("detector",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)!;
    var type=detector.GetType();
    var table=DispatchProxy.Create<IObjectTable,ObjectTableService>();
    type.GetField("objectTable",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(detector,table);
    type.GetField("active",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(detector,true);
    typeof(HmsOriginalAdapter).GetField("fault",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(adapter,"forced capture fault");
    Equal(type.GetMethod("Detect")!.Invoke(detector,null),null);
    Equal(((ObjectTableService)(object)table).LocalReads,1);
});
void OtherInstance()=>InInstalled((target,instance,runtime,adapter)=> {
    var type=target.GetType("HMSync.Services.LocalStateDetector",true)!;
    var other=RuntimeHelpers.GetUninitializedObject(type);
    HmsShadowActor.BeginAllowed=false;
    var before=runtime.Cleanups;
    Equal(type.GetMethod("Detect")!.Invoke(other,null),null);
    Equal(runtime.Cleanups,before);
});
void InstallDev() {
    var context=new AssemblyLoadContext("HMS-dev-adapter-offline",true);
    try {
        var target=context.LoadFromAssemblyPath(dev);
        InInstalled((assembly,instance,runtime,adapter)=> {
            var type=assembly.GetType("HMSync.Services.LocalStateDetector",true)!;
            var detector=instance.GetType().GetField("detector",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance);
            Equal(type.GetMethod("Detect")!.Invoke(detector,null),null);
        },target);
    }
    finally {context.Unload();}
}
void SemanticComparisons() {
    foreach(var method in new[]{"OnCommand","DoMinion","DoAccessory"}) {
        var input=RealIl("HMSync.HMSyncPlugin",method);
        var output=((IEnumerable<CodeInstruction>)Invoke("ReplaceSemanticReads",input,typeof(Fixture).GetMethod(nameof(Fixture.Semantic))!,method)!).ToList();
        Equal(output.Count(i=>i.operand is MethodInfo m && m.Name is "CurrentMountId" or "CurrentMinionId" or "CurrentOrnamentId"),0);
        Equal(output.Count(i=>Equals(i.operand,typeof(Fixture).GetMethod(nameof(Fixture.Semantic)))),1);
    }
}
void CoreRejects()=>InInstalled((target,instance,runtime,adapter)=> {
    runtime.HasActivePreview=true;
    var type=instance.GetType();
    type.GetField("objectTable",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,NullService.New<IObjectTable>());
    type.GetField("chat",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(instance,NullService.New<IChatGui>());
    type.GetMethod("PlayResolvedEmoteCore",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,new object[]{(ushort)47,(ushort)3,(ushort)3,"offline"});
    Equal(runtime.EvidenceCompleted,1); Equal(runtime.Rejections,1); Equal(runtime.Accepted,0);
});
void DiscoverInstance() {
    var dalamud=typeof(IDalamudPluginInterface).Assembly;
    var exposedType=dalamud.GetType("Dalamud.Plugin.ExposedPlugin",true)!;
    var exposed=RuntimeHelpers.GetUninitializedObject(exposedType);
    var field=exposedType.GetFields(BindingFlags.NonPublic|BindingFlags.Instance).SingleOrDefault(f=>f.FieldType.FullName=="Dalamud.Plugin.Internal.Types.LocalPlugin")
        ??throw new Exception("Missing raw plugin field: "+string.Join(",",exposedType.GetFields(BindingFlags.NonPublic|BindingFlags.Instance).Select(f=>f.Name)));
    var local=RuntimeHelpers.GetUninitializedObject(field.FieldType);
    field.SetValue(exposed,local);
    var hms=RuntimeHelpers.GetUninitializedObject(AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="HMSync").GetType("HMSync.HMSyncPlugin",true)!);
    field.FieldType.GetField("instance",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(local,hms);
    var resolved=typeof(HmsOriginalAdapter).GetMethod("FindInstance",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new[]{exposed});
    Equal(ReferenceEquals(resolved,hms),true);
}
public static class Fixture { public static nint Address(object value)=>0; public static void Marker() {} public static ushort Semantic(object value)=>0; }
