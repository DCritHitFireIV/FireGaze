#if !HAS_NATIVE_READBACK
Console.Error.WriteLine("FAIL: no native nonblocking readback adapter exists");
return 1;
#else
using FireGaze.PortraitPreview;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using System.Runtime.InteropServices;

int checks=0;
void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
bool Throws(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
Check(PortraitPixelReadbackD3D11.LayoutMatches(),"exact x64 D3D11 layouts verified before native acquisition");
checks+=NativeAbiCases.Run();
var api=new FakeNative();nint device=50;
var backend=new PortraitPixelReadbackD3D11(()=>device,api);
var wrap=new Wrap();var source=backend.CreateSource(wrap);var lease=source.Retain();
Check(wrap.Shares.Count==1&&lease.Width==2&&lease.Height==2&&lease.DxgiFormat==0,"retain independent wrapper without native probing");
Check(api.Calls==0,"retaining never touches D3D11 outside Draw");
Check(Throws(()=>backend.CreateTransfer(lease))&&api.Calls==0,"CreateTransfer outside public Draw scope fails closed");
IPortraitPixelReadbackTransfer transfer;
using(backend.EnterDraw()){
 transfer=backend.CreateTransfer(lease);
 Check(lease.DxgiFormat==29&&api.CopyCalls==0,"resolve typed sRGB in Draw without submitting a GPU copy");
 Check(api.Staging is {Width:2,Height:2,MipLevels:1,ArraySize:1,SampleCount:1,SampleQuality:0,Usage:3,BindFlags:0,CPUAccessFlags:0x20000,MiscFlags:0},"staging descriptor has exact CPU read-only ABI");
 transfer.Submit();
 Check(api.CopyCalls==1&&api.EndCalls==1,"submit one top-mip copy then one event query");
 Check(transfer.TryMap(out var pending)==PortraitPixelReadbackMapStatus.StillDrawing&&pending is null&&api.MapCalls==0,"pending completion query yields before map");
 api.QueryReady=true;api.Busy=true;
 Check(transfer.TryMap(out pending)==PortraitPixelReadbackMapStatus.StillDrawing&&pending is null,"busy Map yields without blocking fallback");
 Check(api.LastMapFlags==0x100000&&api.LastMapType==1,"Map always READ with DO_NOT_WAIT");
 api.Busy=false;
 Check(transfer.TryMap(out var mapping)==PortraitPixelReadbackMapStatus.Ready&&mapping is not null,"ready map exposes rows");
 var pixels=new byte[16];mapping!.CopyBytes(0,pixels.AsSpan(0,8));mapping.CopyBytes(12,pixels.AsSpan(8,8));
 Check(pixels.SequenceEqual(new byte[]{1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16})&&mapping.DepthPitch==0,"mapped row copying ignores zero DepthPitch and padding");
 Check(!transfer.TryRetire()&&api.Releases==0,"mapped resource cannot retire before Unmap");
 transfer.Unmap();Check(api.Unmaps==1,"unmap occurs once within Draw");
 Check(Throws(()=>mapping.CopyBytes(0,new byte[1])),"mapping becomes unusable immediately after Unmap");
 Check(transfer.TryRetire()&&api.Held==0,"event completion releases every owned native reference");
 Check(transfer.TryRetire()&&api.Releases==api.Acquires,"retirement is idempotent without duplicate Release");
}
lease.Dispose();Check(wrap.Shares[0].Disposes==1&&wrap.Disposes==0,"release independent source without releasing caller wrapper");

// Metadata access can fail after sharing. Transfer ownership first so the core can track release.
{
 var retainedWrap=new Wrap{SharedMetadataFailure=true};
 IPortraitPixelReadbackLease? tracked=null;
 try{tracked=backend.CreateSource(retainedWrap).Retain();}catch(InvalidOperationException){}
 Check(tracked is not null,"share ownership is returned before any fallible wrapper metadata read");
 Check(Throws(()=>_ = tracked!.Width),"metadata failure remains visible to the state machine");
 tracked!.Dispose();Check(retainedWrap.Shares[0].Disposes==1,"metadata failure still has one explicit release owner");
}

// Submitted cancellation/device replacement retains old owned context until its own event completes.
{
 var fake=new FakeNative();nint current=50;var native=new PortraitPixelReadbackD3D11(()=>current,fake);var held=native.CreateSource(new Wrap()).Retain();
 IPortraitPixelReadbackTransfer pending;
 using(native.EnterDraw()){pending=native.CreateTransfer(held);pending.Submit();}
 current=60;native.Invalidate();
 Check(Throws(()=>pending.TryRetire())&&fake.Held>0,"no later Draw means no blind release of pending GPU work");
 using(native.EnterDraw()){
  Check(!pending.TryRetire()&&fake.Releases==0,"changed global device does not fabricate old-context completion");
  fake.QueryReady=true;Check(pending.TryRetire()&&fake.LastQueryContext==500,"retire uses original independently owned context");
 }
 held.Dispose();
}
// Invalid source and partial initialization must remain visible to their returned transfer owner.
foreach(var failure in new[]{"format","dimension","identity","create","ambiguous"}){
 var fake=new FakeNative{Failure=failure};var native=new PortraitPixelReadbackD3D11(()=>50,fake);var held=native.CreateSource(new Wrap()).Retain();
 using(native.EnterDraw()){
  var invalid=native.CreateTransfer(held);
  Check(Throws(invalid.Submit)&&fake.CopyCalls==0,"invalid initialization never submits: "+failure);
  if(failure=="ambiguous")Check(Throws(()=>invalid.TryRetire()),"failed QI with nonzero output cannot be guessed/released");
  else Check(invalid.TryRetire()&&fake.Held==0,"partial owned refs retire before submission: "+failure);
 }
 held.Dispose();
}
// Failure during copy/read/unmap/release must never silently retire uncertain resources.
{
 var fake=new FakeNative{QueryReady=true};var native=new PortraitPixelReadbackD3D11(()=>50,fake);var held=native.CreateSource(new Wrap()).Retain();
 using(native.EnterDraw()){
  var item=native.CreateTransfer(held);item.Submit();item.TryMap(out var mapping);fake.ReadFailure=true;
  Check(Throws(()=>mapping!.CopyBytes(0,new byte[8])),"fallible mapped-memory read propagates failure");
  item.Unmap();fake.ReleaseFailure=true;
  Check(Throws(()=>item.TryRetire()),"uncertain release blocks physical retirement");
  var attempts=fake.Releases;Check(Throws(()=>item.TryRetire())&&fake.Releases==attempts,"uncertain release is not repeated");
 }
 held.Dispose();
}
Console.WriteLine($"Native readback checks: {checks}");return 0;

sealed class Wrap:IDalamudTextureWrap{
 public bool SharedMetadataFailure,MetadataFailure;
 public int Width=>!MetadataFailure?2:throw new InvalidOperationException("metadata failure");public int Height=>2;public ImTextureID Handle=>new(100);public int Disposes;public List<Wrap> Shares=[];
 public IDalamudTextureWrap CreateWrapSharingLowLevelResource(){var w=new Wrap{MetadataFailure=SharedMetadataFailure};Shares.Add(w);return w;}
 public void Dispose()=>Disposes++;
}
sealed class FakeNative:IPortraitPixelReadbackD3D11Native{
 public int Calls,CopyCalls,EndCalls,MapCalls,Unmaps,Acquires,Releases,Held;public bool QueryReady,Busy,ReadFailure,ReleaseFailure;public string? Failure;
 public uint LastMapFlags,LastMapType;public nint LastQueryContext;public PortraitPixelTextureDesc Staging;
 nint Acquire(nint value){Calls++;Acquires++;Held++;return value;}
 public int QueryInterface(nint value,Guid iid,out nint result){
  if(Failure=="ambiguous"){result=999;return -1;}
  if(iid==PortraitPixelReadbackD3D11.Unknown){result=Acquire(90);if(Failure=="identity"&&value==50)result=60;return 0;}
  result=Acquire(value);return 0;
 }
 public nint GetResource(nint value)=>Acquire(200);
 public nint GetDevice(nint value)=>Acquire(55);
 public PortraitPixelTextureDesc GetTextureDesc(nint value){Calls++;return new(2,2,3,1,27,1,0,0,8,0,0);}
 public PortraitPixelSrvDesc GetViewDesc(nint value){Calls++;return new(Failure=="format"?71u:29,Failure=="dimension"?5u:4,0,3,0,0);}
 public nint GetImmediateContext(nint value)=>Acquire(500);
 public uint GetContextType(nint value){Calls++;return 0;}
 public nint CreateStaging(nint device,PortraitPixelTextureDesc descriptor){Staging=descriptor;if(Failure=="create")throw new InvalidOperationException();return Acquire(600);}
 public nint CreateEventQuery(nint device)=>Acquire(700);
 public void CopyTopMip(nint context,nint source,nint destination){Calls++;CopyCalls++;}
 public void End(nint context,nint query){Calls++;EndCalls++;}
 public int GetData(nint context,nint query){Calls++;LastQueryContext=context;return QueryReady?0:1;}
 public int Map(nint context,nint texture,uint mapType,uint flags,out PortraitPixelMapped mapped){Calls++;MapCalls++;LastMapFlags=flags;LastMapType=mapType;mapped=Busy?default:new(800,12,0);return Busy?unchecked((int)0x887A000A):0;}
 public void Unmap(nint context,nint texture){Calls++;Unmaps++;}
 public void CopyBytes(nint address,Span<byte> destination){Calls++;if(ReadFailure)throw new InvalidOperationException();var bytes=new byte[]{1,2,3,4,5,6,7,8,99,99,99,99,9,10,11,12,13,14,15,16};bytes.AsSpan((int)(address-800),destination.Length).CopyTo(destination);}
 public void Release(nint value){Calls++;Releases++;if(ReleaseFailure)throw new InvalidOperationException();Held--;}
}
#endif
