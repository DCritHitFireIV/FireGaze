#if !HAS_PORTRAIT_SESSION
Console.Error.WriteLine("FAIL portrait preview has no isolated session or save guard");
return 1;
#else
using FireGaze.PortraitPreview;

var passed = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException(label);
    passed++;
    Console.WriteLine("PASS " + label);
}
PortraitLook Look(uint head) => new(Enumerable.Range(0, 13)
    .Select(i => new PortraitPiece(i == 2 ? head : (uint)(100+i), 0, 0)).ToArray(), true, true, false);
try
{
    var target = new Target { Look = Look(111) };
    var session = new PortraitPreviewSession(target);
    Check(!session.IsActive && !session.BlockSave(target.Identity), "inactive preview permits native saving");
    Check(session.Open(target.Identity) && session.IsActive, "explicit open captures the editor's wardrobe");
    Check(session.BlockSave(target.Identity), "opening alone blocks save before any wardrobe change");
    Check(!session.BlockSave(target.Identity with { Editor = 99 }), "unrelated editor saves are not intercepted");
    target.Available = false;
    session.Validate();
    Check(session.IsActive && session.BlockSave(target.Identity), "temporary unreadability retains the preview lease and save guard after Framework validation");
    Check(!session.ObserveLook(Look(999)) && session.Current!.Pieces[2].ItemID==111,
        "an unreadable owner cannot publish a new observed appearance");
    var unreadyCalls=target.Applies;
    Check(!session.Replace(PortraitSlot.Head,new(999,0,0)) && !session.SetVisibility(false,false,false)
        && session.IsActive && target.Applies==unreadyCalls,"unready native mutations are refused without retiring the lease");
    Check(!session.Close() && session.IsActive && session.BlockSave(target.Identity) && target.Applies==unreadyCalls,
        "close during temporary unreadability retains restoration and save protection for retry");
    Check(session.BlockSave(target.Identity) && session.IsActive, "save event preserves active guard until Framework lifecycle validation runs");
    target.Available = true;
    Check(session.Replace(PortraitSlot.Head, new(222, 3, 4)), "an owned head item can replace the portrait piece");
    Check(target.Look.Pieces[2] == new PortraitPiece(222, 3, 4), "both dye channels reach the preview target");
    Check(target.RealHead == 111 && target.Pose == 9, "changing portrait equipment leaves real character and pose untouched");
    Check(session.Replace(PortraitSlot.Head, new(333, 0, 0)) && session.Open(target.Identity), "reopening an active session does not replace its original baseline");
    Check(session.Replace(PortraitSlot.MainHand, default), "clearing a preview slot is an explicit zero appearance");
    Check(session.SetVisibility(false, false, true), "head weapon and visor presentation can change independently");
    target.Pose = 17;
    session.Close();
    Check(!session.IsActive && target.Look.Pieces[2].ItemID == 111 && target.Look.Pieces[0].ItemID == 100,
        "close restores entry wardrobe after repeated item choices and clear");
    Check(target.Pose == 17 && target.Look.HeadVisible && target.Look.WeaponVisible && !target.Look.Visor,
        "restore affects wardrobe flags while preserving later portrait camera or pose edits");
    var calls = target.Applies;
    session.Close();
    Check(target.Applies == calls && !session.BlockSave(target.Identity), "close is idempotent and releases save blocking");

    session.Open(target.Identity);
    session.Replace(PortraitSlot.Facewear, new(8, 2, 0));
    Check(target.Look.Pieces[12].ItemID == 8, "facewear has an independent presentation slot");
    target.Identity = target.Identity with { Model = 91, Generation = 2 };
    target.Look = Look(444);
    calls = target.Applies;
    session.Validate();
    Check(!session.IsActive && target.Applies == calls && target.Look.Pieces[2].ItemID == 444,
        "replaced preview allocation invalidates the session without restoring into its successor");
    Check(!session.Replace(PortraitSlot.Body, new(555, 0, 0)), "stale input cannot apply after model replacement");
    Check(session.Open(target.Identity), "a replacement editor can open a fresh independent session");
    target.Refuse = true;
    Check(!session.Replace(PortraitSlot.Head, new(999, 0, 0)) && session.Current!.Pieces[2].ItemID == 444,
        "failed native change cannot replace the current desired appearance");
    var beforeFailedClose = target.Applies;
    session.Close();
    Check(session.IsActive && session.BlockSave(target.Identity) && target.Applies == beforeFailedClose,
        "failed restore retains preview and save guard for an explicit retry");
    var replacementIdentity = target.Identity with { Editor = 101 };
    Check(!session.Open(replacementIdentity) && session.IsActive, "failed cleanup cannot be replaced by a new preview capture");
    target.Refuse = false;
    target.ThrowApply = true;
    session.Close();
    Check(session.IsActive && session.BlockSave(target.Identity), "throwing restore retains preview and save guard without leaking exception");
    target.ThrowApply = false;
    session.Close();
    Check(!session.IsActive && !session.BlockSave(target.Identity), "successful restore retry releases save guard");
    target.Available = false;
    Check(!session.Open(target.Identity) && !session.IsActive, "unavailable model cannot activate save blocking");
    target.Available = true;
    Check(!session.Open(default), "zero owner identity is refused");
    target.DuringApply = () => target.Identity = target.Identity with { Character = 77 };
    Check(session.Open(target.Identity), "valid entry starts before callback replacement");
    Check(!session.Replace(PortraitSlot.Head, new(555, 0, 0)) && !session.IsActive,
        "identity replacement during native apply invalidates subsequent operations");
    target.DuringApply=null;target.Available=true;
    Check(session.Open(target.Identity),"same-owner post-write unreadiness fixture opens");
    target.DuringApply=()=>target.Available=false;
    Check(!session.Replace(PortraitSlot.Head,new(777,0,0)) && session.IsActive && session.BlockSave(target.Identity),
        "readiness lost during a native write retains the restoration lease");
    target.Available=true;target.DuringApply=null;
    Check(session.Close() && !session.IsActive,"same-owner readiness recovery allows the original wardrobe to restore");
    Console.WriteLine($"{passed}/{passed} portrait session checks passed");
    return 0;
}
catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed class Target : IPortraitPreviewTarget
{
    public PortraitIdentity Identity = new(1, 2, 3, 4, 5);
    public PortraitLook Look = null!;
    public bool Available = true;
    public bool Refuse;
    public bool ThrowApply;
    public int Applies;
    public int RealHead = 111;
    public int Pose = 9;
    public Action? DuringApply;
    public bool IsCurrent(PortraitIdentity identity) => Available && identity == Identity;
    public bool IsEditorOwnerDestroyed(PortraitIdentity identity) => identity != Identity;
    public PortraitLook? Capture(PortraitIdentity identity) => IsCurrent(identity) ? Look.Copy() : null;
    public bool Apply(PortraitIdentity identity, PortraitLook look)
    {
        if (!IsCurrent(identity) || Refuse) return false;
        if (ThrowApply) throw new InvalidOperationException("simulated restore failure");
        Applies++;
        Look = look.Copy();
        DuringApply?.Invoke();
        return true;
    }
}
#endif
