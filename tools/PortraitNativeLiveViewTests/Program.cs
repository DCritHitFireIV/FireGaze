#if !HAS_NATIVE_LIVE_VIEW
Console.Error.WriteLine("FAIL: acquire-only native live view has no owned publish / Draw-borrow / retirement lifecycle");
return 1;
#else
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

var checks = 0;
var failures = new List<string>();
void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
void Case(string name, Action action)
{
    try { action(); Console.WriteLine("PASS: " + name); }
    catch (Exception error) { failures.Add(name + ": " + error.Message); Console.Error.WriteLine("FAIL: " + failures[^1]); }
}

Case("disabled and disposed view never acquires a native source", () =>
{
    var source = new Source(); var view = new PortraitNativeLiveView(source);
    view.Update(); Check(source.Calls == 0 && !view.TryBorrow(out _), "default-off must not acquire or publish");
    view.SetEnabled(true); view.Dispose(); view.Update(); view.SetEnabled(true);
    Check(source.Calls == 0 && !view.TryBorrow(out _), "disposed view cannot be restarted");
});
Case("current native image is borrowed with padding excluded", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    Check(view.TryBorrow(out var borrow), "an enabled valid source publishes");
    using (borrow)
    {
        Check(borrow!.TryGetImage(out var image) && image.Handle.Handle == 123, "Draw receives the retained SRV only");
        Check(image.UV0 == Vector2.Zero && image.UV1 == new Vector2(.78125f, .9765625f), "400x500 content in a 512x512 RT excludes padding");
        Check(image.PixelWidth == 400 && image.PixelHeight == 500 && image.FrameNumber == 1, "image has the native aspect and an update stamp");
    }
});
Case("same SRV is acquired and displayed again on every update", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    view.TryBorrow(out var first); first!.TryGetImage(out var oldImage); view.Update();
    Check(!first.TryGetImage(out _) && source.Textures[0].Disposes == 0, "replacement invalidates old Draw access without disposing a borrowed lease");
    Check(view.TryBorrow(out var second), "an unchanged handle is still published for the new frame");
    using (second) Check(second!.TryGetImage(out var image) && image.Handle.Handle == oldImage.Handle.Handle && image.FrameNumber == 2, "same handle is not mistaken for an unchanged static image");
    first.Dispose(); first.Dispose(); Check(source.Textures[0].Disposes == 1, "retired lease is released once after last borrow");
});
Case("disable retires immediately and waits for all Draw borrowers", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    view.TryBorrow(out var one); view.TryBorrow(out var two); view.SetEnabled(false); view.Update();
    Check(!one!.TryGetImage(out _) && !two!.TryGetImage(out _) && !view.TryBorrow(out _), "disabled image becomes inaccessible to old and new draws");
    one.Dispose(); Check(source.Textures[0].Disposes == 0, "one remaining draw keeps its resource alive");
    two!.Dispose(); Check(source.Textures[0].Disposes == 1 && source.Calls == 1, "last draw releases resource and off updates do not acquire");
});
Case("invalidation clears a published image without a new native call", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    view.TryBorrow(out var old); source.Invalidate();
    Check(!old!.TryGetImage(out _) && !view.TryBorrow(out _) && source.Calls == 1, "lifecycle event instantly stops stale display");
    old.Dispose(); Check(source.Textures[0].Disposes == 1, "invalidated borrowed resource retires safely");
});
Case("reentrant disable during acquire cannot publish its result", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true);
    source.OnAcquire = () => view.SetEnabled(false); view.Update();
    Check(!view.TryBorrow(out _) && source.Textures.Single().Disposes == 1, "an acquire ticket is cancelled by disable");
});
Case("reentrant invalidation during acquire cannot publish its result", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true);
    source.OnAcquire = source.Invalidate; view.Update();
    Check(!view.TryBorrow(out _) && source.Textures.Single().Disposes == 1, "old generation cannot arrive after invalidation");
});
Case("failed source and off-thread update stop the previous image", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    source.Fail = true; view.Update(); Check(!view.TryBorrow(out _) && source.Textures[0].Disposes == 1, "failed acquire removes old display");
    source.Fail = false; view.Update(); source.IsFrameworkThread = false; view.Update();
    Check(source.Calls == 3 && !view.TryBorrow(out _) && source.Textures[1].Disposes == 1, "off-thread update does not call source and invalidates display");
});
Case("acquisition exceptions do not leave old content visible", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    source.OnAcquire = () => throw new InvalidOperationException("source unavailable"); view.Update();
    Check(!view.TryBorrow(out _) && source.Textures[0].Disposes == 1, "exception fails closed");
});
Case("malformed dimensions and dead texture handle cannot publish", () =>
{
    foreach (var dimensions in new[] { (0u, 500u, 512u, 512u), (513u, 500u, 512u, 512u), (400u, 500u, 0u, 512u), (400u, 500u, 16385u, 512u) })
    {
        var source = new Source { Dimensions = dimensions }; using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
        Check(!view.TryBorrow(out _) && source.Textures.Single().Disposes == 1, "invalid native dimensions release candidate");
    }
    var zero = new Source { Handle = 0 }; using var live = new PortraitNativeLiveView(zero); live.SetEnabled(true); live.Update();
    Check(!live.TryBorrow(out _) && zero.Textures.Single().Disposes == 1, "null handle is never drawn");
});
Case("source owner and descriptor changes replace and retire old content", () =>
{
    var source = new Source(); using var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update(); view.TryBorrow(out var old);
    source.Key = Source.KeyA with { Editor = Source.KeyA.Editor with { Generation = 2 }, Epoch = 2, Image = 31 };
    source.Handle = 456; source.Dimensions = (300, 400, 512, 512); view.Update();
    Check(!old!.TryGetImage(out _), "owner/generation/node drift prevents old draw");
    using (var current = GetBorrow(view)) Check(current.TryGetImage(out var image) && image.Handle.Handle == 456 && image.UV1 == new Vector2(.5859375f,.78125f), "fresh node descriptor replaces dimensions and SRV");
    old.Dispose(); Check(source.Textures[0].Disposes == 1, "old generation is retired once");
});
Case("concurrent replacement cannot dispose an active Draw borrow", () =>
{
    var source = new Source(); var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    var draw = GetBorrow(view); Task.Run(view.Dispose).GetAwaiter().GetResult();
    Check(source.Textures.Single().Disposes == 0 && !draw.TryGetImage(out _), "teardown invalidates but waits for Draw");
    draw.Dispose(); view.Dispose(); Check(source.Textures.Single().Disposes == 1, "cross-thread teardown releases after Draw completion");
});
Case("native node source proves owner before and after bounds and retain", () => NativeCases(Check));
Case("native live physical retirement includes acquisition prefix and outstanding Draw borrowers", () =>
{
    var source = new Source(); var view = new PortraitNativeLiveView(source); view.SetEnabled(true);
    Task? prefixDrain = null; var prefixHeld = false;
    source.OnAcquire = () => { view.Dispose(); prefixDrain = PhysicalDrain(view); prefixHeld = !prefixDrain.IsCompleted; };
    view.Update();
    Check(prefixHeld && prefixDrain!.IsCompletedSuccessfully && source.Textures.Single().Disposes == 1,
        "reentrant acquisition cannot retire provider scope until its late frame is released");
    source = new Source(); view = new(source); view.SetEnabled(true); view.Update();
    var borrow = GetBorrow(view); view.Dispose(); var drain = PhysicalDrain(view);
    Check(!drain.IsCompleted && source.Textures.Single().Disposes == 0,
        "physical retirement waits for the final outstanding Draw borrower");
    borrow.Dispose();
    Check(drain.IsCompletedSuccessfully && source.Textures.Single().Disposes == 1,
        "returning the last Draw borrower releases resource before host retirement completes");
});
Case("native live failed release quarantines ownership and prevents another acquisition", () =>
{
    var source = new Source { OnCreated = value => value.OnDispose = () => throw new IOException("unconfirmed native live release") };
    var view = new PortraitNativeLiveView(source); view.SetEnabled(true); view.Update();
    view.SetEnabled(false); view.SetEnabled(true); view.Update(); view.Dispose();
    Check(!PhysicalDrain(view).IsCompleted && source.Calls == 1 && source.Textures.Single().Disposes == 1,
        "failed release holds host provider scope without retry or further native acquisition");
});
NativeLiveControllerCases.Run(Case, Check);
NativeLiveFeatureWiringCases.Run(Case, Check);
Console.WriteLine($"PortraitNativeLiveViewTests: {checks} checks, {failures.Count} failed");
return failures.Count == 0 ? 0 : 1;

static PortraitNativeLiveBorrow GetBorrow(PortraitNativeLiveView view)
    => view.TryBorrow(out var value) && value is not null ? value : throw new InvalidOperationException("expected borrow");
static Task PhysicalDrain(object owner) => owner.GetType().GetMethod("DrainPhysicalAsync")?.Invoke(owner, null) as Task ?? Task.CompletedTask;

static unsafe void NativeCases(Action<bool,string> check)
{
    AgentBannerEditor editor=default; AgentBannerEditorState state=default; CharaViewPortrait view=default;
    AtkUnitBase addon=default; AtkComponentPortrait component=default; AtkImageNode image=default;
    AtkUldPartsList parts=default; AtkUldPart part=default; AtkUldAsset asset=default; Texture kernel=default;
    editor.EditorState=&state; editor.AddonId=42; state.AgentBannerEditor=&editor; state.CharaView=&view;
    view.State=6; view.CharaViewPortraitCharacterLoaded=true; addon.Id=42; addon.IsVisible=true; addon.Flags1A1|=1;
    var nodes=stackalloc AtkResNode*[1]; nodes[0]=(AtkResNode*)&image;
    component.UldManager.NodeList=nodes; component.UldManager.NodeListCount=1; component.CharacterImageNode=&image;
    image.Type=NodeType.Image; image.NodeFlags=NodeFlags.Visible; image.ScaleX=image.ScaleY=1; image.PartsList=&parts;
    image.Color.R=image.Color.G=image.Color.B=image.Color.A=255; parts.PartCount=1; parts.Parts=&part; part.UldAsset=&asset;
    asset.AtkTexture.TextureType=TextureType.KernelTexture; asset.AtkTexture.KernelTexture=&kernel;
    kernel.AllocatedWidth=kernel.AllocatedHeight=512; kernel.ActualWidth=400; kernel.ActualHeight=500; kernel.D3D11ShaderResourceView=(void*)123;
    var api=new NodeApi { Identity=new((nint)(&editor),(nint)(&state),(nint)(&view),1,900), Addon=(nint)(&addon), Component=(nint)(&component) };
    using var source=new PortraitNativeLiveNodeSource(api);
    check(source.TryAcquire(out var frame) && frame is not null,"fresh real node structure can acquire retained SRV");
    check(frame!.TryDescribe(1,out var description) && description.UV1==new Vector2(.78125f,.9765625f),"source excludes RT padding"); frame.Dispose();
    var baseCalls=api.Textures.Count;
    api.OnBounds=()=>api.Identity=api.Identity with { Generation=2 };
    check(!source.TryAcquire(out _) && api.Textures.Count==baseCalls,"generation drift during bounds never retains old descriptor");
    api.OnBounds=null; api.OnRetain=()=>api.Owner=new(2,3);
    check(!source.TryAcquire(out _) && api.Textures[^1].Disposes==1,"owner drift during retain releases the new official lease");
    api.OnRetain=null; var changedKernel=(nint)(&kernel);
    api.OnRetain=()=>((Texture*)changedKernel)->D3D11ShaderResourceView=(void*)456;
    check(!source.TryAcquire(out _) && api.Textures[^1].Disposes==1,"SRV drift during retain rejects and releases candidate");
    api.OnRetain=null; api.OnBounds=()=>source.Invalidate();
    check(!source.TryAcquire(out _),"source epoch change during native callback rejects candidate");
    api.OnBounds=null; api.ThrowRetain=true;
    check(!source.TryAcquire(out _),"failed public retain fails closed"); api.ThrowRetain=false;
    kernel.D3D11ShaderResourceView=(void*)123;
    var findCalls=0;
    api.OnFind=()=> { if(++findCalls==6)((Texture*)changedKernel)->D3D11ShaderResourceView=(void*)789; };
    var described=source.TryAcquire(out frame) && frame is not null && frame.TryDescribe(1,out description);
    check(!described || description.Handle.Handle==(ulong)kernel.D3D11ShaderResourceView,
        "no owner callback after final descriptor validation may publish an obsolete SRV");
    frame?.Dispose(); api.OnFind=null;
    var addonReads=0; var componentLookups=0; var beforeInvalidation=api.Textures.Count;
    api.OnAddon=()=> { if(++addonReads==2)source.Invalidate(); };
    api.OnFind=()=>componentLookups++;
    check(!source.TryAcquire(out _) && componentLookups==0 && api.Textures.Count==beforeInvalidation,
        "invalidation inside final addon callback stops before old native structures or component lookup are used");
    api.OnAddon=null; api.OnFind=null;
    api.OnCreated=value=>value.OnDispose=()=>throw new IOException("unconfirmed rejected native SRV");
    api.OnRetain=()=>api.Owner=new(api.Owner.AccountID+1,api.Owner.ContentID+1);
    var rejectedView=new PortraitNativeLiveView(source);rejectedView.SetEnabled(true);rejectedView.Update();rejectedView.Dispose();
    check(!PhysicalDrain(rejectedView).IsCompleted,"native source rejected-owner cleanup failure reaches the host retirement quarantine");
    api.OnCreated=null;api.OnRetain=null;
    var before=api.Textures.Count; source.Dispose();
    check(!source.TryAcquire(out _) && api.Textures.Count==before,"disposed node source cannot reacquire");
}

sealed class TextureLease(nint handle=123,int width=512,int height=512):IDalamudTextureWrap
{
    public ImTextureID Handle=>new(handle); public int Width=>width; public int Height=>height;
    public int Disposes; public Action? OnDispose; public void Dispose(){Interlocked.Increment(ref Disposes);OnDispose?.Invoke();}
}
sealed class Source:IPortraitNativeLiveSource
{
    public static readonly PortraitNativeLiveKey KeyA=new(new(1,2),new(10,11,12,1,13),20,21,30,1);
    public event Action? Invalidated;
    public bool IsFrameworkThread {get;set;}=true; public int Calls; public bool Fail; public Action? OnAcquire;
    public nint Handle=123; public PortraitNativeLiveKey Key=KeyA;
    public (uint ActualWidth,uint ActualHeight,uint Width,uint Height) Dimensions=(400,500,512,512);
    public List<TextureLease> Textures=[];
    public Action<TextureLease>? OnCreated;
    public void Invalidate()=>Invalidated?.Invoke();
    public bool TryAcquire(out PortraitNativeLiveFrame? frame)
    {
        Calls++; frame=null; if(Fail)return false; OnAcquire?.Invoke();
        var d=Dimensions; var texture=new TextureLease(Handle,(int)d.Width,(int)d.Height); Textures.Add(texture);OnCreated?.Invoke(texture);
        frame=new(Key,texture,d.Width,d.Height,d.ActualWidth,d.ActualHeight,uint.MaxValue); return true;
    }
}
sealed class NodeApi:IPortraitNativeLiveNodeApi
{
    public bool IsFrameworkThread=>true; public PortraitIdentity Identity; public PortraitCaptureOwner Owner=new(1,2);
    public nint Addon; public nint Component; public Action? OnBounds; public Action? OnRetain; public Action? OnFind; public Action? OnAddon; public bool ThrowRetain;
    public List<TextureLease> Textures=[];
    public Action<TextureLease>? OnCreated;
    public PortraitIdentity? ReadReadyEditor()=>Identity;
    public PortraitCaptureOwner ReadOwner()=>Owner;
    public nint ReadEditorAddon() { OnAddon?.Invoke(); return Addon; }
    public nint FindPortrait(nint addon,Func<bool> current) { OnFind?.Invoke(); return current()?Component:0; }
    public PortraitCaptureRect? ReadNodeBounds(nint node) { OnBounds?.Invoke(); return new(0,0,160,256); }
    public IDalamudTextureWrap Retain(nint handle,int width,int height)
    {
        if(ThrowRetain)throw new InvalidOperationException("retain unavailable");
        var result=new TextureLease(handle,width,height); Textures.Add(result);OnCreated?.Invoke(result); OnRetain?.Invoke(); return result;
    }
}
#endif
