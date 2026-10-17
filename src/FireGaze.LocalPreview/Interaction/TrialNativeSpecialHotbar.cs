using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FireGaze.LocalPreview.Runtime;
using HotbarSlot=FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using SlotType=FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace FireGaze.LocalPreview.Interaction;

/// <summary>Temporarily lends the game's existing PetHotbar to local cosmetic actions.
/// Only transient slots and UI presentation mode are changed. SetAndSaveSlot, HOTBAR.DAT,
/// ActionManager, player collection fields and combat action replacement are never used.</summary>
public sealed unsafe class TrialNativeSpecialHotbar : IDisposable
{
    // Current CN mouse callbacks and HOTBAR_EX input both end at this (module, slot*)
    // dispatcher. FFCS ExecuteSlot currently aliases the three-argument ExecuteSlotById
    // ABI and must not be called. This reviewed function returns U1 bool.
    public const string ExecuteSlotSignature="48 89 5C 24 08 48 89 6C 24 20 56 57 41 56 48 83 EC 40 0F B6 82 C7 00 00 00 48 8B F2 FF C8 C6 44 24 68 00 48 8B F9 41 B6 01 83 F8 23";
    private const uint TrialMode=0xE; // native special bar enabled, no player mount/ornament flag
    private const int UiInputFlagsOffset=0x2AD4C;
    private const ushort PetKeyboardInput=4;
    [return:MarshalAs(UnmanagedType.U1)]
    private delegate bool ExecuteSlot(RaptureHotbarModule* module,HotbarSlot* slot);
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly Func<int,long,bool> activate;
    private readonly TrialNativeHotbarLease lease=new();
    private Hook<ExecuteSlot>? execute;
    private TrialSpecialBar? bar;
    private bool disposed;
    private bool listenerRegistered;
    private bool available;
    private bool inside;
    private long consumed;
    private ushort originalKeyboardInput;
    public bool Available=>!disposed && available;
    public bool Active=>Available && lease.Held;
    public long InputsConsumed=>consumed;
    // FFCS structures contain fields rejected by runtime marshaling. Even taking a
    // field address through a null native pointer raises NullReferenceException on
    // the current JIT. Check the actual unmanaged layout on owned memory instead.
    public static bool LayoutValid
    {
        get
        {
            if(sizeof(HotbarSlot)!=TrialNativeHotbarLease.SlotStride)return false;
            var module=(RaptureHotbarModule*)NativeMemory.Alloc((nuint)sizeof(RaptureHotbarModule));
            try
            {
                return module!=null && (byte*)(&module->PetHotbar)-(byte*)module==TrialNativeHotbarLease.PetSlotOffset;
            }
            finally{NativeMemory.Free(module);}
        }
    }
    public string Status {get;private set;}="原生特殊栏尚未初始化";

    public TrialNativeSpecialHotbar(IFramework framework,IGameGui gameGui,IAddonLifecycle lifecycle,
        IGameInteropProvider interop,IPluginLog log,Func<int,long,bool> activate)
    {
        this.framework=framework;this.gameGui=gameGui;this.lifecycle=lifecycle;this.log=log;this.activate=activate;
        try
        {
            if(!LayoutValid)
                throw new InvalidOperationException("当前客户端特殊栏布局不匹配");
            execute=interop.HookFromSignature<ExecuteSlot>(ExecuteSlotSignature,Execute);
            execute.Enable();
            listenerRegistered=true;
            lifecycle.RegisterListener(AddonEvent.PostUpdate,"_ActionBarEx",OnAddon);
            lifecycle.RegisterListener(AddonEvent.PostRequestedUpdate,"_ActionBarEx",OnAddon);
            available=true;Status="原生特殊栏已就绪";
        }
        catch(Exception ex)
        {
            RemoveCallbacksAndHook();Status="原生特殊栏接口不可用";log.Warning(ex,"[LocalPreview] Native special bar unavailable.");
        }
    }

    public void Publish(TrialSpecialBar? current)
    {
        if(!framework.IsInFrameworkUpdateThread || disposed)return;
        if(bar?.Revision!=current?.Revision || bar?.Id!=current?.Id || bar?.IsMount!=current?.IsMount)Release();
        bar=current;
        if(current is null){Release();return;}
        try{Refresh();}
        catch(Exception ex){Release();available=false;Status="原生特殊栏暂不可用";log.Warning(ex,"[LocalPreview] Native special bar publish failed.");}
    }

    /// <summary>Development probe through the real client hotbar dispatcher. No input is
    /// synthesized. The caller must be on Framework and the selected transient slot must
    /// still belong to this exact trial. Physical mouse/key acceptance is a separate check.</summary>
    public bool ExecuteNativeSlot(int index)
    {
        if(!Active || !framework.IsInFrameworkUpdateThread || index is <0 or >TrialSpecialHotbarModel.ExitSlot
            || !Resolve(out var module,out _,out var identity) || module->PetHotbarMode!=TrialMode)return false;
        var slot=module->PetHotbar.GetHotbarSlot((uint)index);
        if(slot==null || !lease.CanInvokeNativeSlot(identity,index,Read(slot)))return false;
        // This THREE-argument generated ABI is reviewed on the current CN binary.
        // The similarly named two-argument ExecuteSlot binding is incorrect there.
        return module->ExecuteSlotById(18,(uint)index)!=0;
    }

    private bool Resolve(out RaptureHotbarModule* module,out AtkUnitBase* addon,out TrialNativeHotbarIdentity identity)
    {
        module=RaptureHotbarModule.Instance();
        addon=gameGui.GetAddonByName<AtkUnitBase>("_ActionBarEx",1);
        var source=Control.GetLocalPlayer();
        identity=source==null || bar==null?default:new((nint)module,(nint)addon,addon==null?(ushort)0:addon->Id,
            (nint)source,*(uint*)((byte*)source+0x74),source->Mount.MountId,source->OrnamentData.OrnamentId,bar.Revision);
        return module!=null && addon!=null && addon->RootNode!=null && identity.Valid;
    }

    private static TrialNativeHotbarCommand Read(HotbarSlot* slot)=>new((byte)slot->CommandType,slot->CommandId);
    private static TrialNativeHotbarCommand CommandFor(TrialSpecialAction action)
        =>action.Kind==TrialSpecialActionKind.FlightPose?new((byte)SlotType.GeneralAction,2):new((byte)SlotType.Action,action.Id);
    private static void Write(HotbarSlot* slot,TrialNativeHotbarCommand command)
        =>slot->Set((SlotType)command.Type,command.Id);

    private void Refresh()
    {
        if(!Available || bar is not {} current || inside || !lease.CanAcquire(current.Revision))return;
        if(!Resolve(out var module,out var addon,out var identity))
        {if(lease.Held){lease.Suspend(current.Revision);Release();}Status="等待角色与原生特殊栏";return;}
        if(lease.Held && !lease.CanRestore(identity)){lease.Suspend(current.Revision);Release();Status="原生角色状态已改变，等待本地试用收尾";return;}
        if(!lease.Held)
        {
            var before=new TrialNativeHotbarCommand[16];var expected=new TrialNativeHotbarCommand[16];
            for(var i=0;i<16;i++)before[i]=Read(module->PetHotbar.GetHotbarSlot((uint)i));
            for(var i=0;i<current.Actions.Count && i<6;i++)
                if(current.Actions[i] is {} action)expected[i]=CommandFor(action);
            expected[TrialSpecialHotbarModel.ExitSlot]=new((byte)SlotType.GeneralAction,23);
            if(!lease.Acquire(identity,before,expected,module->PetHotbarMode,addon->IsVisible))return;
            originalKeyboardInput=(ushort)(*(ushort*)((byte*)module+UiInputFlagsOffset)&PetKeyboardInput);
            inside=true;
            try
            {
                for(var i=0;i<16;i++)
                {
                    if(!Resolve(out var currentModule,out _,out var currentIdentity) || !lease.CanRestore(currentIdentity))
                    {lease.Suspend(current.Revision);return;}
                    Write(currentModule->PetHotbar.GetHotbarSlot((uint)i),expected[i]);
                }
                if(!Resolve(out module,out addon,out identity) || !lease.CanRestore(identity))
                {lease.Suspend(current.Revision);return;}
                module->PetHotbarMode=TrialMode;
                *(ushort*)((byte*)module+UiInputFlagsOffset)|=PetKeyboardInput;
                addon->IsVisible=!gameGui.GameUiHidden;
                Status="正在使用游戏原生特殊栏";
            }
            finally{inside=false;if(!lease.CanAcquire(current.Revision))Release();}
        }
        else
        {
            // A newly populated native bar wins. Do not overwrite genuine slot updates.
            for(var i=0;i<16;i++)if(!lease.IsExpected(i,Read(module->PetHotbar.GetHotbarSlot((uint)i))))
            {lease.Suspend(current.Revision);Release();Status="原生特殊栏已被游戏更新";return;}
            if(module->PetHotbarMode!=TrialMode){lease.Suspend(current.Revision);Release();Status="原生特殊栏模式已被游戏更新";return;}
            if(!gameGui.GameUiHidden)addon->IsVisible=true;
        }
    }

    private void OnAddon(AddonEvent eventType,AddonArgs args)
    {
        if(disposed || !framework.IsInFrameworkUpdateThread || bar==null || inside)return;
        try{Refresh();}
        catch(Exception ex){Release();available=false;Status="原生特殊栏暂不可用";log.Warning(ex,"[LocalPreview] Native special bar refresh failed.");}
    }

    private bool Execute(RaptureHotbarModule* module,HotbarSlot* slot)
    {
        if(!disposed && available && !inside && framework.IsInFrameworkUpdateThread && bar is {} current
            && module!=null && slot!=null && Resolve(out var actual,out var addon,out var identity)
            && module==actual && module->PetHotbarMode==TrialMode && addon->IsVisible
            && lease.TryRoute(identity,(nint)slot,Read(slot),out var index))
        {
            consumed++;
            // Even blank leased slots are consumed, preventing the prior real item's action.
            // The callback owns the matching trial revision and is already on Framework.
            if(index<=TrialSpecialHotbarModel.ExitSlot)
                try{activate(index,current.Revision);}catch(Exception ex){log.Warning(ex,"[LocalPreview] Native special action failed.");}
            return true;
        }
        return execute!.Original(module,slot);
    }

    private void Release()
    {
        if(!lease.Held)return;
        try
        {
            Resolve(out var module,out var addon,out var identity);
            if(module!=null && lease.CanRestoreCommands(identity))
            {
                inside=true;
                try
                {
                    for(var i=0;i<16;i++)
                    {
                        // Set can re-enter other plugin code. Re-resolve singletons/allocation
                        // identity before every native call; never dereference a retained addon.
                        Resolve(out module,out addon,out identity);
                        if(module==null || !lease.CanRestoreCommands(identity))break;
                        var slot=module->PetHotbar.GetHotbarSlot((uint)i);
                        if(lease.IsExpected(i,Read(slot)))Write(slot,lease.Original(i));
                    }
                    // A new genuine collection owns its UI mode and visibility immediately.
                    var hasAddon=Resolve(out module,out addon,out identity);
                    // A partial native slot update does not transfer unchanged trial-owned
                    // UI fields. A genuine new mode does, and must keep its input/visibility.
                    if(module!=null && lease.CanRestoreModule(identity) && module->PetHotbarMode==TrialMode)
                    {
                        module->PetHotbarMode=lease.OriginalMode;
                        var flags=(ushort*)((byte*)module+UiInputFlagsOffset);
                        if((*flags&PetKeyboardInput)!=0)*flags=(ushort)((*flags&~PetKeyboardInput)|originalKeyboardInput);
                        if(hasAddon && lease.CanRestorePresentation(identity) && addon->IsVisible && !lease.OriginalVisible)addon->IsVisible=false;
                    }
                }
                finally{inside=false;}
            }
        }
        finally{lease.Clear();}
    }

    public void Dispose()
    {
        if(disposed)return;
        if(framework.IsInFrameworkUpdateThread)DisposeCore();
        else framework.RunOnFrameworkThread(DisposeCore).GetAwaiter().GetResult();
    }

    private void DisposeCore()
    {
        if(disposed)return;
        // Stop addon callbacks before restoration can re-enter other plugin code.
        disposed=true;available=false;
        try{Release();}
        catch(Exception ex){WarnCleanup(ex);}
        finally{bar=null;RemoveCallbacksAndHook();}
    }

    private void RemoveCallbacksAndHook()
    {
        if(listenerRegistered)
        {
            listenerRegistered=false;
            try{lifecycle.UnregisterListener(AddonEvent.PostUpdate,"_ActionBarEx",OnAddon);}
            catch(Exception ex){WarnCleanup(ex);}
            try{lifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate,"_ActionBarEx",OnAddon);}
            catch(Exception ex){WarnCleanup(ex);}
        }
        var hook=execute;execute=null;
        try{hook?.Dispose();}catch(Exception ex){WarnCleanup(ex);}
    }

    private void WarnCleanup(Exception ex)
    {
        try{log.Warning(ex,"[LocalPreview] Native special bar cleanup failed.");}catch{}
    }
}
