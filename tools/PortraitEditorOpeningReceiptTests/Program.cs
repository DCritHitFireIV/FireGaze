#if OPENING_RECEIPT
using FireGaze.PortraitPreview;
#endif
#if !OPENING_RECEIPT
throw new Exception("FAIL: no synchronous native-opening receipt; async slash ACK cannot prove ownership.");
#else
return Tests.Run();
internal static class Tests
{
    private static int checks;
    private static void Check(bool ok,string label) { checks++; if(!ok) throw new Exception("FAIL: "+label); }
    public static int Run()
    {
        using var output = Console.Out;
        OpeningAvailabilityCases.Run(Check);
        {
            var f=new Fixture();
            Check(f.Calls==0 && f.Reads==0,"construction is offline");
            var one=f.Receipt.Read(); var two=f.Receipt.Read();
            Check(one.Status=="NotExecuted" && one==two && f.Reads==0,"pure getter does not sample or manufacture execution");
            Check(f.Open(),"zero to new roots is synchronously attributed");
            var receipt=f.Receipt.Read();
            Check(receipt.Status=="ReturnedPending" && receipt.Nonce==f.Nonce && f.Calls==1,"returned means native invoked once, not Ready");
            Check(receipt.Before!.State==0 && receipt.Returned!.State==20 && receipt.ReadyIdentity is null,"receipt binds zero/new roots and does not invent character");
            Check(!receipt.MayMutate && !receipt.MayClose,"receipt never grants mutation or close authority");
            f.Current=f.Current with { ReadyIdentity=new(10,20,30,7,40), AgentActive=true };
            f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Ready" && f.Receipt.Read().ReadyIdentity!.Generation==7,"later Ready must match synchronously bound roots");
            var before=f.Reads; _=f.Receipt.Read();_=f.Receipt.Read(); Check(before==f.Reads,"Ready getters never advance lifecycle");
            f.Current=f.Current with { PreviewActive=true, OperationsIdle=false }; f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Ready","owned preview/capture work does not revoke object ownership by itself");
        }
        {
            var f=new Fixture();f.AfterInvoke=()=>{};Check(!f.Open(),"native return without new roots is unproven");
            f.Current=f.Opened; f.Receipt.Update();
            Check(f.Receipt.Read().Status=="UnprovenReturn" && f.Receipt.Read().ReadyIdentity is null,"late unrelated roots are never adopted");
        }
        foreach(var mutation in new Func<PortraitEditorOpeningSample,PortraitEditorOpeningSample>[] {
            s=>s with { IsFrameworkThread=false }, s=>s with { State=20 },s=>s with { AgentActive=true },
            s=>s with { PreviewActive=true },s=>s with { OperationsIdle=false },s=>s with { SaveGuardReady=false },
            s=>s with { Source=s.Source! with { Generation=0 } },s=>s with { Source=s.Source! with { ContentID=0 } },
            s=>s with { Conditions=[1] },s=>s with { Conditions=Fixture.Flags(1,5) },
            s=>s with { Source=s.Source! with { IsMoving=true } },s=>s with { Source=s.Source! with { IsCasting=true } },
        }) {
            var f=new Fixture();f.Current=mutation(f.Current); Check(!f.Open() && f.Calls==0,"invalid closed baseline cannot invoke native");
        }
        foreach(var mutation in new Func<PortraitEditorOpeningSample,PortraitEditorOpeningSample>[] {
            s=>s with { Agent=11 },s=>s with { StateOwner=11 },s=>s with { ViewOwner=11 },s=>s with { View=0 },
            s=>s with { OpenType=2 },s=>s with { OpenerSourceID=4 },s=>s with { ThreadID=9 },s=>s with { Frame=2 },
            s=>s with { Source=s.Source! with { Generation=10 } },s=>s with { Source=s.Source! with { AccountID=88 } },
            s=>s with { Source=s.Source! with { TerritoryID=9 } },s=>s with { Conditions=Fixture.Flags(1,5,100) },
        }) {
            var f=new Fixture(); f.Opened=mutation(f.Opened); Check(!f.Open() && f.Calls==1 && f.Receipt.Read().Status=="Invalidated","changed source/thread/roots/entry during native call cannot bind");
        }
        foreach(var mutation in new Func<PortraitEditorOpeningSample,PortraitEditorOpeningSample>[] {
            s=>s with { State=21 },s=>s with { View=31 },s=>s with { Source=s.Source! with { ContentID=90 } },
            s=>s with { Source=s.Source! with { Address=91 } },s=>s with { Conditions=Fixture.Flags(1) },
            s=>s with { ReadyIdentity=new(10,20,30,0,40) },s=>s with { ReadyIdentity=new(10,20,30,7,100) },
        }) {
            var f=new Fixture();f.Open(); f.Current=mutation(f.Current);f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Invalidated","pending owner change or malformed Ready permanently invalidates");
        }
        {
            var f=new Fixture();f.Open();f.Current=f.Current with { ReadyIdentity=new(10,20,30,7,40),AgentActive=true };f.Receipt.Update();
            f.Current=f.Current with { ReadyIdentity=new(10,20,30,8,40) };f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Invalidated","same address with a new allocation generation invalidates");
        }
        foreach(var reason in new[]{"BannerEditor PreFinalize","Logout","TerritoryChanged","Disposed","Unreceipted editor operation"}) {
            var f=new Fixture();f.Open();var epoch=f.Receipt.Read().Epoch;f.Receipt.Invalidate(reason);
            f.Current=f.Current with { ReadyIdentity=new(10,20,30,7,40) };f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Invalidated" && f.Receipt.Read().Epoch>epoch,"lifecycle revoke cannot be revived: "+reason);
        }
        {
            var f=new Fixture(); f.AfterInvoke=()=>f.Receipt.Invalidate("PreFinalize inside native call");
            Check(!f.Open() && f.Receipt.Read().Status=="Invalidated","reentrant finalize cannot be overwritten by native return");
        }
        {
            var f=new Fixture();f.BeforeRead=()=>f.Receipt.Invalidate("PreFinalize inside baseline read");
            Check(!f.Open() && f.Calls==0 && f.Receipt.Read().Status=="Invalidated",
                "baseline read reentry cannot overwrite revocation or invoke native open");
        }
        {
            var f=new Fixture();f.BeforeRead=()=>{f.BeforeRead=null;f.Receipt.Open("plate",Guid.NewGuid().ToString("D"),()=>f.Calls++);};
            Check(!f.Open() && f.Calls==0 && f.Receipt.Read().Status=="Invalidated",
                "nested command during baseline sampling cannot invoke any native opening");
        }
        {
            var f=new Fixture(); f.AfterInvoke=()=>{f.Receipt.Open("plate",Guid.NewGuid().ToString("D"),()=>f.Calls++);};
            Check(!f.Open() && f.Calls==1,"nested opening attempt cannot invoke twice or publish outer success");
        }
        {
            var f=new Fixture();f.Open();f.Current=Fixture.Closed();
            Check(!f.Open() && f.Calls==1,"nonce replay cannot invoke native twice");
            Check(!f.Receipt.Open("gearset","bad",()=>f.Calls++),"malformed nonce rejected without native call");
        }
        {
            var f=new Fixture();f.Open();f.Now=11;f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Invalidated","pending creation expires by monotonic budget");
        }
        {
            var f=new Fixture();f.Open();var detached=f.Receipt.Read();detached.Before!.Conditions[1]=0;
            Check(f.Receipt.Read().Before!.Conditions[1]==1,"returned receipt arrays are detached");
        }
        {
            var f=new Fixture();f.AfterInvoke=()=>throw new InvalidOperationException("native failed");
            Check(!f.Open() && f.Receipt.Read().Status=="Invalidated" && f.Calls==1,"throwing invocation is recorded without retry or cleanup");
        }
        {
            var f=new Fixture();
            for(var attempt=0;attempt<128;attempt++) {
                f.Current=Fixture.Closed();
                Check(f.Receipt.Open("gearset",Guid.NewGuid().ToString("D"),()=>{f.Calls++;f.Current=f.Opened;}),"bounded diagnostic attempt is available");
            }
            f.Current=Fixture.Closed();
            Check(!f.Receipt.Open("gearset",Guid.NewGuid().ToString("D"),()=>f.Calls++) && f.Calls==128,
                "finite nonce capacity refuses new native calls without eviction or lifetime growth");
        }
        {
            var f=new Fixture();
            var field=typeof(PortraitEditorOpeningController).GetField("receipt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            field.SetValue(f.Receipt,f.Receipt.Read() with { Epoch=long.MaxValue });
            f.Receipt.Invalidate("epoch exhausted");f.Receipt.Invalidate("repeat revoke");
            Check(f.Receipt.Read().Epoch==0 && !f.Open() && f.Calls==0 && f.Receipt.Read().Epoch==0,
                "epoch exhaustion is permanent and cannot resurrect through initialization rules");
        }
        {
            var f=new Fixture();f.Opened=f.Opened with { ViewOwner=0 };
            Check(f.Open(),"audited native contract permits a null view Agent backlink on synchronous return");
            f.Current=f.Current with { ViewOwner=10,ReadyIdentity=new(10,20,30,7,40),AgentActive=true };f.Receipt.Update();
            Check(f.Receipt.Read().Status=="Ready","null view Agent may bind to the same editor while fixed roots prove cause");
        }
        {
            var feature=new PortraitPreviewFeature();
            var nonce=Guid.NewGuid().ToString("D");
            var first=feature.Receipt;_=feature.Receipt;
            Check(feature.Reads==0 && first.Status=="NotExecuted","real Feature getter and initialization do not read native state");
            feature.Open("gearset",nonce);
            Check(feature.Calls==1 && feature.Receipt.Status=="ReturnedPending" && feature.Receipt.Nonce==nonce,
                "real Feature command adapter invokes core in controller's causal callback");
            feature.Current=feature.Current with { ReadyIdentity=new(10,20,30,7,40),AgentActive=true };feature.Tick();
            Check(feature.Receipt.Status=="Ready","real Feature Update progresses an already bound allocation");
            var reads=feature.Reads;_=feature.Receipt;_=feature.Receipt;
            Check(reads==feature.Reads,"real Feature receipt getter remains pure after Ready");
            feature.Release();
            Check(feature.Receipt.Status=="Invalidated" && FireGaze.Plugin.AddonLifecycle.Listeners.Count==0,
                "real Feature disposal invalidates ownership and detaches lifecycle callback");
        }
        foreach(var lifecycle in new Action[]{FireGaze.Plugin.AddonLifecycle.FinalizeEditor,
            FireGaze.Plugin.ClientState.LogOut,FireGaze.Plugin.ClientState.ChangeTerritory}) {
            var feature=new PortraitPreviewFeature();feature.Open("plate",Guid.NewGuid().ToString("D"));
            lifecycle();feature.Tick();
            Check(feature.Receipt.Status=="Invalidated" && feature.Calls==1,"actual Feature lifecycle callback permanently revokes receipt");
            feature.Release();
        }
        {
            var feature=new PortraitPreviewFeature();feature.DuringNative=FireGaze.Plugin.AddonLifecycle.FinalizeEditor;
            feature.Open("gearset",Guid.NewGuid().ToString("D"));
            Check(feature.Receipt.Status=="Invalidated" && feature.Calls==1 && feature.Errors==1,
                "real Feature reentrant finalize cannot be overwritten by native return or reported as opening success");
            feature.Release();
        }
        Console.WriteLine($"PASS: {checks} native-opening receipt checks");return 0;
    }
}
internal sealed class Fixture
{
    internal int Reads,Calls; internal double Now; internal string Nonce=Guid.NewGuid().ToString("D");
    internal PortraitEditorOpeningSample Current=Closed();
    internal PortraitEditorOpeningSample Opened=Closed() with { State=20,View=30,StateOwner=10,ViewOwner=10,OpenType=1,OpenerSourceID=3,Conditions=Flags(1,100) };
    internal Action? AfterInvoke,BeforeRead; internal PortraitEditorOpeningController Receipt;
    internal Fixture() { Receipt=new(()=>{Reads++;BeforeRead?.Invoke();return Current;},()=>Now);AfterInvoke=()=>Current=Opened; }
    internal bool Open()=>Receipt.Open("gearset",Nonce,()=>{Calls++;AfterInvoke!();});
    internal static int[] Flags(params int[] flags)=>Enumerable.Range(0,112).Select(i=>flags.Contains(i)?1:0).ToArray();
    internal static PortraitEditorOpeningSample Closed()=>new(true,1,1,10,0,0,0,0,false,-1,-1,3,
        new(100,9,77,88,178,18,1,2,3,1000,false,false,false,0),Flags(1),false,true,true,null);
}
#endif
