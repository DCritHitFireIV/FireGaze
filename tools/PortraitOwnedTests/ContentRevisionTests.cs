using FireGaze.PortraitPreview;

internal static class ContentRevisionTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string name){if(ok)pass++;else{fail++;Console.WriteLine($"FAIL content-revision {name}");}}
  void Case(string name,Action test){try{test();}catch(Exception e){fail++;Console.WriteLine($"FAIL content-revision {name}: {e.GetType().Name} {e.Message}");}}
  long Revision(OwnedEquipmentCatalog catalog)=>catalog.ContentRevision;
  var owner=new OwnedCharacterKey(1,101);var other=new OwnedCharacterKey(1,202);
  var bag=new OwnedSourceKey(OwnedSourceKind.Inventory);var dresser=new OwnedSourceKey(OwnedSourceKind.GlamourDresser);
  var now=DateTimeOffset.Parse("2026-10-19T00:00:00Z");
  OwnedEquipmentCatalog New()=>new(id=>id is 123 or 234 or 1000123 ? id%1000000:null);
  Case("unread state does not request persistence but a first authoritative empty read does",()=>{
   var catalog=New();catalog.ActivateCharacter(owner);catalog.RegisterSource(bag);catalog.MarkUnavailable(owner,bag,"not loaded");
   Check(Revision(catalog)==0,"unread and failure are not content changes");
   catalog.ApplyRead(owner,bag,now,[]);Check(Revision(catalog)==1,"first successful empty read is durable content");
   catalog.ApplyRead(owner,bag,now.AddSeconds(2),[]);Check(Revision(catalog)==1,"same empty read does not schedule another write");
   Check(catalog.BuildView(now.AddSeconds(2)).Sources.Single().ReadAt==now.AddSeconds(2),"duplicate read still refreshes displayed read time");
   catalog.MarkUnavailable(owner,bag,"closed");Check(Revision(catalog)==1,"unavailable transition does not create durable content");
  });
  Case("complete physical content changes drive revisions independent of observation order",()=>{
   var catalog=New();catalog.ActivateCharacter(owner);
   catalog.ApplyRead(owner,bag,now,[new(123,1,0,1,2,3),new(234,1,1)]);Check(Revision(catalog)==1,"first complete items read");
   catalog.ApplyRead(owner,bag,now.AddSeconds(2),[new(234,1,1),new(123,1,0,1,2,3)]);Check(Revision(catalog)==1,"container enumeration order is not a content change");
   catalog.ApplyRead(owner,bag,now.AddSeconds(4),[new(123,1,0,1,2,4),new(234,1,1)]);Check(Revision(catalog)==2,"second dye change is durable");
   catalog.ApplyRead(owner,bag,now.AddSeconds(6),[new(123,1,0,2,2,4),new(234,1,1)]);Check(Revision(catalog)==3,"quantity change is durable");
   catalog.ApplyRead(owner,bag,now.AddSeconds(8),[new(123,1,0,2,2,4,234),new(234,1,1)]);Check(Revision(catalog)==4,"glamour metadata change is durable");
   catalog.ApplyRead(owner,bag,now.AddSeconds(10),[new(1000123,1,0,2,2,4,234,true),new(234,1,1)]);Check(Revision(catalog)==5,"raw quality change retained despite normalized item id");
   catalog.ApplyRead(owner,bag,now.AddSeconds(12),[new(1000123,1,2,2,2,4,234,true),new(234,1,1)]);Check(Revision(catalog)==6,"physical location change is durable");
   catalog.MarkUnavailable(owner,bag,"not loaded");Check(Revision(catalog)==6&&catalog.BuildView(now).Items.Count==2,"failed read retains durable content and revision");
   catalog.ApplyRead(owner,bag,now.AddSeconds(14),[]);Check(Revision(catalog)==7&&catalog.BuildView(now.AddSeconds(14)).Items.Count==0,"successful empty replaces content once");
   catalog.ApplyRead(owner,dresser,now.AddSeconds(14),[]);Check(Revision(catalog)==8,"another source first empty read is independently durable");
  });
  Case("rejected callbacks and imported baselines do not fabricate new content",()=>{
   var original=New();original.ActivateCharacter(owner);original.ApplyRead(owner,bag,now,[new(123,1,0)]);
   var catalog=New();Check(catalog.ImportJSON(original.ExportJSON()),"valid disk baseline imports");catalog.ActivateCharacter(owner);
   Check(Revision(catalog)==0,"disk baseline is already persisted");
   catalog.ApplyRead(owner,bag,now.AddSeconds(2),[new(123,1,0)]);Check(Revision(catalog)==0,"identical imported source refresh does not force disk rewrite");
   Check(!catalog.ApplyRead(owner,bag,now,[new(234,1,0)])&&Revision(catalog)==0,"older observation rejected without revision");
   Check(!catalog.ApplyRead(owner,bag,now.AddSeconds(2),[new(234,1,0)])&&Revision(catalog)==0,"same-time conflicting observation rejected without revision");
   catalog.ActivateCharacter(other);Check(!catalog.ApplyRead(owner,bag,now.AddSeconds(4),[new(234,1,0)])&&Revision(catalog)==0,"old character callback cannot create persistence work");
   catalog.ApplyRead(other,bag,now.AddSeconds(4),[new(123,1,0)]);Check(Revision(catalog)==1,"same item on another owner is different durable content");
  });
  return(pass,fail);
 }
}
