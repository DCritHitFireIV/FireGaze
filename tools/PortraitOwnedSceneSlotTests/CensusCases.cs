using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

internal static unsafe class CensusCases
{
#if !SLOT_CENSUS
    internal static int Run() { Console.Error.WriteLine("FAIL: coherent eight-slot census is absent; TryReadIdle false cannot distinguish an unknown occupant from unreadable/changed evidence."); return 1; }
#else
    private static int checks;
    internal static int Run()
    {
        EverySourceOffsetKeepsItsIdentity(); MixedRowsPreserveUnknownWithoutDereference(); WholeCensusRejectsPartialAndDrift(); UnqualifiedAndExpiredReadNothing();
        Console.WriteLine($"PortraitOwnedSceneSlotCensusCases: {checks} checks passed; self-allocated SDK layouts only."); return 0;
    }
    private static void EverySourceOffsetKeepsItsIdentity()
    {
        for (uint source=0;source<8;source++)
        {
            using var f=Ready(source);
            Check(f.Reader.TryReadCensus(f.Phase,out var census) && census.Rows.Count==8
                && census.Rows[(int)source].Kind==PortraitOwnedSceneSlotCensusKind.Source
                && census.Rows.Where(row=>row.Slot!=source).All(row=>row.Kind==PortraitOwnedSceneSlotCensusKind.ProvedIdle),
                "source is preserved and excluded from idle at every table offset "+source);
        }
    }
    private static void MixedRowsPreserveUnknownWithoutDereference()
    {
        using var f = Ready();
        f.Entry(1)->BattleChara = (BattleChara*)1; f.Entry(1)->ObjectKind = (ObjectKind)1;
        *(nint*)(f.Phase.Roots.SceneCameras + 0xA0 + 2*8) = (nint)0xBAD;
        *(int*)f.CountAddress(4) = 2;
        f.Entry(5)->ObjectKind = (ObjectKind)1;
        f.Entry(7)->BattleChara = f.Actor; f.Entry(7)->ObjectKind = (ObjectKind)1;
        f.Entry(0)->Index=0xFA; f.Entry(0)->MemoryIndex=241;
        Check(f.Reader.TryReadCensus(f.Phase, out var census), "coherent first census preserves preexisting unknown occupants");
        Check(census is { Complete: true, ProductionAdmission: false } && census.Rows.Count == 8, "complete detached DTO contains all eight rows without a scene lease");
        var want = new[] { PortraitOwnedSceneSlotCensusKind.ProvedIdle, PortraitOwnedSceneSlotCensusKind.OccupiedUnknown,
            PortraitOwnedSceneSlotCensusKind.OccupiedUnknown, PortraitOwnedSceneSlotCensusKind.Source,
            PortraitOwnedSceneSlotCensusKind.OccupiedUnknown, PortraitOwnedSceneSlotCensusKind.OccupiedUnknown,
            PortraitOwnedSceneSlotCensusKind.ProvedIdle, PortraitOwnedSceneSlotCensusKind.OccupiedUnknown };
        for (var index=0; index<8; index++) Check(census.Rows[index].Slot == index && census.Rows[index].Kind == want[index], "literal mixed classification and slot offset " + index);
        Check(census.Phase == f.Phase && census.Rows[1].Character == (nint)1 && census.Rows[2].RegisteredCamera == (nint)0xBAD && census.Rows[4].ActiveCount == 2,
            "unknown means unadopted numerical metadata, never a guessed identified consumer");
        Check(!f.Memory.Attempts.Contains((nint)1) && !f.Memory.Attempts.Contains((nint)0xBAD)
            && !f.Memory.Attempts.Contains((nint)f.Actor + 0x74), "unreadable foreign actor/camera and even readable stranger generation are not dereferenced");
        Check(census.Rows[0].RetainedOffscreenCamera == (nint)0x50000 && census.Rows[0].RetainedViewCamera == (nint)0x60000 && census.Rows[0].RenderActive,
            "idle proof permits retained camera pointers and stale active flag, preserving audited idle semantics");
        Check(census.Rows[0].ObjectIndex==0xFA && census.Rows[0].MemoryIndex==241,
            "raw object entry metadata is detached without turning stale index bytes into occupancy");
        Check(census.Rows is not PortraitOwnedSceneSlotCensusRow[], "published rows cannot be mutated as the caller-owned work array");
    }
    private static void WholeCensusRejectsPartialAndDrift()
    {
        for (uint slot=0;slot<8;slot++)
        {
            using var f=Ready(); f.Memory.RejectAddress=f.CountAddress(slot);
            Reject(f,"unreadable raw row refuses whole census " + slot);
        }
        using (var f=Ready()) { *(int*)f.CountAddress(5)=-1; Reject(f,"negative native count is incomplete evidence, never a known occupied consumer"); }
        foreach (var round in new[] { 1,2 })
        {
            using var f=Ready(); var seen=0;
            f.Memory.AfterRead = address => { if (address == f.CountAddress(7) && ++seen==round) f.Source->State=5; };
            Reject(f,"source binding is rechecked after complete round " + round);
        }
        using (var f=Ready())
        {
            var fired=false; f.Memory.AfterRead=address=> { if (!fired && address==f.CountAddress(7)) { fired=true; f.Entry(0)->ObjectKind=(ObjectKind)1; } };
            Reject(f,"late first-round takeover of earlier row refuses mixed-age census");
        }
        using (var f=Ready())
        {
            var seen=0; f.Memory.AfterRead=address=> { if (address==f.CountAddress(0) && ++seen==2) *(int*)f.CountAddress(7)=1; };
            Reject(f,"second-round takeover of later row cannot preserve earlier idle classification");
        }
        foreach (var change in new Func<PortraitOwnedSceneSlotRoots,PortraitOwnedSceneSlotRoots>[] {
            r=>r with { Renderer=r.Renderer+16 },r=>r with { SceneCameras=r.SceneCameras+16 },r=>r with { Offscreen=r.Offscreen+16 },
            r=>r with { RenderTargets=r.RenderTargets+16 },r=>r with { ClientObjects=r.ClientObjects+16 } })
        {
            using var f=Ready(); f.Memory.AfterRead=address=> { if (address==f.CountAddress(7)) f.Memory.Roots=change(f.Memory.Roots); };
            Reject(f,"any manager/root replacement during row copy refuses entire census");
        }
        foreach (var change in new Func<PortraitOwnedSceneSlotPhase,PortraitOwnedSceneSlotPhase>[] {
            p=>p with { NativeFrame=p.NativeFrame+1 },p=>p with { PhaseToken=p.PhaseToken+1 },p=>p with { ManagerGeneration=p.ManagerGeneration+1 },
            p=>p with { SessionEpoch=p.SessionEpoch+1 },p=>p with { Source=p.Source with { Generation=p.Source.Generation+1 } } })
        {
            using var f=Ready(); f.Memory.AfterRead=address=> { if (address==f.CountAddress(7)) f.Phases.Current=change(f.Phase); };
            Reject(f,"phase/source/resource generation replacement during copy refuses entire census");
        }
        foreach (var mutate in new Action<Fixture>[] {
            f=>f.SourceActor->DrawData.OwnerObject=(Character*)f.Actor,
            f=>*(uint*)((byte*)f.SourceActor+0x74)=20,
            f=>f.State->CharaView=f.Owned,
            f=>f.Editor->EditorState=null,
        }) { using var f=Ready(); mutate(f); Reject(f,"invalid original source identity cannot be downgraded to unknown/idle"); }
    }
    private static void UnqualifiedAndExpiredReadNothing()
    {
        foreach (var change in new Func<PortraitOwnedSceneSlotPhase,PortraitOwnedSceneSlotPhase>[] {
            p=>p with { InNativePhase=false },p=>p with { BuildValidated=false },p=>p with { ThreadID=p.ThreadID+1 },
            p=>p with { PhaseToken=0 },p=>p with { SessionEpoch=0 },p=>p with { ManagerGeneration=0 },p=>p with { Source=default },
        })
        {
            using var f=Ready(); var invalid=change(f.Phase); f.Phases.Current=invalid;
            Check(!f.Reader.TryReadCensus(invalid,out var census) && !census.Complete && f.Memory.Reads==0, "unqualified census performs no native read");
        }
        using (var f=Ready())
        {
            f.Phases.Current=f.Phase with { PhaseToken=f.Phase.PhaseToken+1 };
            Check(!f.Reader.TryReadCensus(f.Phase,out _) && f.Memory.Reads==0,"stale detached phase is rejected before root reads");
        }
        using (var f=Ready())
        {
            var okay=Task.Run(()=>f.Reader.TryReadCensus(f.Phase,out _)).GetAwaiter().GetResult();
            Check(!okay && f.Memory.Reads==0,"real foreign thread cannot consume saved census phase or probe roots");
        }
        using (var f=Ready())
        {
            var phases=new PortraitOwnedSceneSlotNativePhaseSource(f.Memory,()=>true,()=>true,()=>1);
            var reader=new PortraitOwnedSceneSlotReader(f.Memory,phases);
            var scope=phases.EnterSource(f.Phase.Source,1,1);
            Check(scope is not null && phases.TryCapture(out var issued),"actual source scope issues a phase"); phases.TryCapture(out issued);
            Check(reader.TryReadCensus(issued,out var census) && census.Complete,"actual issued native source scope permits complete detached census");
            scope!.Dispose(); var reads=f.Memory.Reads;
            Check(!reader.TryReadCensus(issued,out _) && f.Memory.Reads==reads,"exiting actual native source scope expires all census permission with zero later native reads");
            using var fresh=phases.EnterSource(f.Phase.Source,2,1); phases.TryCapture(out issued);
            using (phases.BeginResourceMutation())
            {
                reads=f.Memory.Reads; Check(!reader.TryReadCensus(issued,out _) && f.Memory.Reads==reads,"actual resource mutation revokes census before any copy");
            }
        }
    }
    private static Fixture Ready(uint sourceSlot=3)
    {
        var f=new Fixture();
        for (uint slot=0;slot<8;slot++)
        {
            *f.Entry(slot)=default; *(int*)f.CountAddress(slot)=0;
            *(nint*)(f.Phase.Roots.SceneCameras+0xA0+(nint)(slot*8))=0;
            *((byte*)f.RenderManager+0x3837C+slot)=1;
            *(nint*)(f.Phase.Roots.Offscreen+0xC8+(nint)(slot*8))=(nint)(0x50000+slot*16);
            *(nint*)((byte*)f.RenderManager+0x2738+slot*24)=(nint)(0x60000+slot*16);
        }
        f.SetIdle((sourceSlot+7)%8); return f;
    }
    private static void Reject(Fixture f,string message)
    {
        Check(!f.Reader.TryReadCensus(f.Phase,out var census) && census is { Complete:false,ProductionAdmission:false }
            && census.Rows.Count==8 && census.Rows.All(row=>row.Kind==PortraitOwnedSceneSlotCensusKind.IncompleteOrChanged && row.Character==0),message+"; no partial classified rows escape");
    }
    private static void Check(bool valid,string message) { checks++; if (!valid) throw new Exception("FAIL: "+message); }
#endif
}
