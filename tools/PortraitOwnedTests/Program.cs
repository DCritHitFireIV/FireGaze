using FireGaze.PortraitPreview;

var passed = 0;
var failed = 0;
var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
var alice = new OwnedCharacterKey(11, 101);
var bob = new OwnedCharacterKey(11, 202);
var sameContentOtherAccount = new OwnedCharacterKey(22, 101);
var bag = new OwnedSourceKey(OwnedSourceKind.Inventory);
var dresser = new OwnedSourceKey(OwnedSourceKind.GlamourDresser);
var retainerA = new OwnedSourceKey(OwnedSourceKind.RetainerInventory, 501);
var retainerB = new OwnedSourceKey(OwnedSourceKind.RetainerInventory, 502);
OwnedEquipmentCatalog New() => new(itemID => itemID is >= 100 and <= 999 ? itemID : itemID is >= 1000100 and <= 1000999 ? itemID - 1000000 : null);
OwnedItemRecord Item(uint itemID, int slot = 0, byte dye1 = 0, byte dye2 = 0, uint glamour = 0, uint quantity = 1, uint container = 0) => new(itemID, container, slot, quantity, dye1, dye2, glamour, false);
void Check(bool ok, string name) { if (ok) { passed++; } else { failed++; Console.WriteLine($"FAIL {name}"); } }
void Case(string name, Action test) { try { test(); } catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.GetType().Name} {e.Message}"); } }
void Throws<T>(Action action, string name) where T:Exception { try { action(); Check(false,name); } catch(T) { Check(true,name); } }

Case("unread source remains distinct from read empty", () => {
 var c=New(); c.ActivateCharacter(alice); c.RegisterSource(bag); c.RegisterSource(dresser);
 var v=c.BuildView(now); Check(v.Items.Count==0,"no fabricated items"); Check(v.Sources.Count==2,"two sources"); Check(v.Sources.All(s=>s.Status==OwnedReadStatus.Unread),"unread initially");
 c.ApplyRead(alice,bag,now,[]); v=c.BuildView(now);
 Check(v.Sources.Single(s=>s.Source==bag).Status==OwnedReadStatus.Current,"read empty is current");
 Check(v.Sources.Single(s=>s.Source==dresser).Status==OwnedReadStatus.Unread,"unread dresser not empty");
});
Case("only true equipment is catalogued and glamour is metadata", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,bag,now,[Item(123,glamour:999),Item(2,slot:1),Item(0,slot:2)]);
 var v=c.BuildView(now); Check(v.Items.Count==1,"non equipment filtered"); Check(v.Items[0].ItemID==123,"actual ownership retained");
 Check(v.Items[0].Variants[0].GlamourID==999,"glamour metadata retained"); Check(v.Items.All(i=>i.ItemID!=999),"glamour not treated as owned");
});
Case("dedupe retains physical locations and distinct dual dyes", () => {
 var c=New(); c.ActivateCharacter(alice);
 c.ApplyRead(alice,bag,now,[Item(123,0,2,3,quantity:2),Item(123,1,4,5)]);
 c.ApplyRead(alice,dresser,now,[Item(1000123,0,2,3)]);
 var e=c.BuildView(now).Items.Single(); Check(e.ItemID==123,"HQ normalization"); Check(e.Variants.Count==3,"all locations kept");
 Check(e.Variants.Sum(v=>v.Quantity)==4,"quantity retained"); Check(e.Variants.Any(v=>v.Stain1==4 && v.Stain2==5),"dual dyes kept");
 Check(e.Variants.Select(v=>v.Source).Distinct().Count()==2,"two sources retained");
});
Case("unavailable sources keep historical contents without becoming empty", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,dresser,now,[Item(234)]);
 c.MarkUnavailable(alice,dresser,"容器未打开"); var v=c.BuildView(now);
 Check(v.Items.Single().ItemID==234,"stale cache remains"); Check(v.Sources.Single().Status==OwnedReadStatus.PossiblyStale,"closed source stale");
 Check(v.Sources.Single().Reason=="容器未打开","reason retained");
 c.ApplyRead(alice,dresser,now.AddMinutes(1),[]); Check(c.BuildView(now.AddMinutes(1)).Items.Count==0,"successful empty replaces cache");
});
Case("source snapshots replace atomically and ignore older observations", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,bag,now,[Item(123)]);
 Throws<ArgumentException>(()=>c.ApplyRead(alice,bag,now.AddSeconds(1),[Item(234),Item(345)]),"duplicate physical slot rejected");
 Check(c.BuildView(now).Items.Single().ItemID==123,"invalid snapshot leaves prior items");
 c.ApplyRead(alice,bag,now.AddSeconds(-1),[Item(345)]); Check(c.BuildView(now).Items.Single().ItemID==123,"old observation ignored");
 c.ApplyRead(alice,bag,now.AddSeconds(2),[Item(456)]); Check(c.BuildView(now.AddSeconds(2)).Items.Single().ItemID==456,"new read replaces old");
});
Case("accounts characters and retainers never cross contaminate", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,retainerA,now,[Item(123)]); c.ApplyRead(alice,retainerB,now,[Item(234)]);
 c.ActivateCharacter(bob); Check(c.BuildView(now).Items.Count==0,"other character empty"); c.ApplyRead(bob,bag,now,[Item(345)]);
 c.ActivateCharacter(sameContentOtherAccount); Check(c.BuildView(now).Items.Count==0,"other account empty");
 c.ActivateCharacter(alice); Check(c.BuildView(now).Items.Count==2,"first character restored");
 Check(c.BuildView(now).Sources.All(s=>s.Status==OwnedReadStatus.PossiblyStale),"reactivation marks stale");
 Check(c.BuildView(now).Items.SelectMany(i=>i.Variants).Select(v=>v.Source.RetainerID).Order().SequenceEqual(new ulong[]{501,502}),"retainer itemIDs retained");
});
Case("late character callbacks rejected", () => {
 var c=New(); c.ActivateCharacter(alice); c.ActivateCharacter(bob);
 Check(!c.ApplyRead(alice,bag,now,[Item(123)]),"wrong character callback rejected"); Check(c.BuildView(now).Items.Count==0,"wrong data absent");
 Check(!c.MarkUnavailable(alice,bag,"old callback"),"wrong status rejected");
});
Case("age and future clock data never look current", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,bag,now,[Item(123)]);
 Check(c.BuildView(now.AddMinutes(16)).Sources.Single().Status==OwnedReadStatus.PossiblyStale,"older than threshold stale");
 Check(c.BuildView(now.AddSeconds(-1)).Sources.Single().Status==OwnedReadStatus.PossiblyStale,"future observation stale");
});
Case("cache jsonText keeps all owners and imports stale", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,bag,now,[Item(123,0,7,9,glamour:999)]); c.RegisterSource(dresser);
 c.ActivateCharacter(bob); c.ApplyRead(bob,retainerA,now,[Item(234)]);
 var jsonText=c.ExportJSON(); var copy=New(); Check(copy.ImportJSON(jsonText),"import accepted"); copy.ActivateCharacter(alice);
 var v=copy.BuildView(now); Check(v.Items.Single().ItemID==123,"owner roundtrip"); Check(v.Items.Single().Variants.Single().Stain2==9,"dye roundtrip");
 Check(v.Sources.Single(s=>s.Source==bag).Status==OwnedReadStatus.PossiblyStale,"import stale");
 Check(v.Sources.Single(s=>s.Source==dresser).Status==OwnedReadStatus.Unread,"unread roundtrip");
 copy.ActivateCharacter(bob); Check(copy.BuildView(now).Items.Single().ItemID==234,"second owner roundtrip");
});
Case("invalid import does not destroy current cache", () => {
 var c=New(); c.ActivateCharacter(alice); c.ApplyRead(alice,bag,now,[Item(123)]);
 Check(!c.ImportJSON("{bad"),"bad jsonText rejected"); Check(c.BuildView(now).Items.Single().ItemID==123,"bad jsonText preserves cache");
 Check(!c.ImportJSON("{\"Version\":999,\"Characters\":[]}"),"unknown schema rejected");
 Check(c.BuildView(now).Items.Single().ItemID==123,"schema error preserves cache");
});
Case("invalid identities and unsupported sources rejected", () => {
 var c=New(); Throws<ArgumentException>(()=>c.ActivateCharacter(new(0,101)),"zero account invalid");
 Throws<ArgumentException>(()=>c.ActivateCharacter(new(11,0)),"zero content invalid");
 c.ActivateCharacter(alice);
 Throws<ArgumentException>(()=>c.RegisterSource(new(OwnedSourceKind.RetainerInventory)),"retainer itemID required");
 Throws<ArgumentException>(()=>c.RegisterSource(new(OwnedSourceKind.Inventory,501)),"player source no retainer");
 Throws<ArgumentException>(()=>c.RegisterSource(new((OwnedSourceKind)999)),"unknown source rejected");
});
Case("read payload is detached from caller memory", () => {
 var c=New(); c.ActivateCharacter(alice); var list=new List<OwnedItemRecord>{Item(123)}; c.ApplyRead(alice,bag,now,list); list.Clear();
 Check(c.BuildView(now).Items.Single().ItemID==123,"caller collection cannot mutate cache");
});
var nativeResult=NativeReadTests.Run(); passed+=nativeResult.Passed;failed+=nativeResult.Failed;
var fileResult=CacheFileTests.Run();passed+=fileResult.Passed;failed+=fileResult.Failed;
var writerResult=CacheWriterTests.Run();passed+=writerResult.Passed;failed+=writerResult.Failed;
var gateResult=RetainerGateTests.Run();passed+=gateResult.Passed;failed+=gateResult.Failed;
Console.WriteLine($"PortraitOwnedTests: {passed} passed, {failed} failed");
Environment.ExitCode=failed==0 ? 0 : 1;
