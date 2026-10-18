using FireGaze.PortraitPreview;

internal static class CacheSavePolicyTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string name){if(ok)pass++;else{fail++;Console.WriteLine($"FAIL save-policy {name}");}}
  void Case(string name,Action test){try{test();}catch(Exception e){fail++;Console.WriteLine($"FAIL save-policy {name}: {e.GetType().Name} {e.Message}");}}
  OwnedCacheSavePolicy Policy(OwnedCacheWriter writer,long baseline=0)=>new(writer,baseline);
  bool Queue(OwnedCacheSavePolicy policy,OwnedEquipmentCatalog catalog)=>policy.QueueIfNeeded(catalog);
  bool Flush(OwnedCacheWriter writer)=>writer.FlushAsync().GetAwaiter().GetResult();
  var directory=Path.Combine(Path.GetTempPath(),"firegaze-owned-save-policy-"+Guid.NewGuid().ToString("N"));
  var owner=new OwnedCharacterKey(1,101);var bag=new OwnedSourceKey(OwnedSourceKind.Inventory);var now=DateTimeOffset.Parse("2026-10-19T00:00:00Z");
  OwnedEquipmentCatalog New()=>new(id=>id==123?id:null);
  try{
   Case("first complete source is saved immediately without close or periodic timer",()=>{
    var catalog=New();catalog.ActivateCharacter(owner);var file=new OwnedEquipmentCacheFile(Path.Combine(directory,"immediate"));file.Load(catalog,out _);
    using var writer=new OwnedCacheWriter(snapshot=>file.SaveJSON(snapshot,out _));var policy=Policy(writer);
    catalog.RegisterSource(bag);catalog.MarkUnavailable(owner,bag,"not loaded");Check(!Queue(policy,catalog),"unread source does not write empty ownership");
    catalog.ApplyRead(owner,bag,now,[]);Check(Queue(policy,catalog)&&Flush(writer),"first successful empty source persists before logout");
    var restored=New();Check(file.Load(restored,out _),"source cache already readable before dispose");restored.ActivateCharacter(owner);
    Check(restored.BuildView(now).Sources.Single().ReadAt==now&&restored.BuildView(now).Items.Count==0,"authoritative empty distinguished from unread on disk");
    catalog.ApplyRead(owner,bag,now.AddSeconds(2),[]);catalog.MarkUnavailable(owner,bag,"closed");
    Check(!Queue(policy,catalog)&&writer.CompletedWrites==1,"read time and failure state alone do not create repeated writes");
    catalog.ApplyRead(owner,bag,now.AddSeconds(4),[new(123,1,0,1,4,5)]);
    Check(Queue(policy,catalog)&&Flush(writer),"changed complete source persists immediately");
    Check(file.Load(restored,out _)&&restored.BuildView(now).Items.Single().Variants.Single().Stain2==5,"latest dual dye content is durable without unload");
   });
   Case("failed save retries identical content rather than marking it clean",()=>{
    var catalog=New();catalog.ActivateCharacter(owner);var file=new OwnedEquipmentCacheFile(Path.Combine(directory,"retry"));file.Load(catalog,out _);var attempts=0;
    using var writer=new OwnedCacheWriter(snapshot=>Interlocked.Increment(ref attempts)==1?false:file.SaveJSON(snapshot,out _));var policy=Policy(writer);
    catalog.ApplyRead(owner,bag,now,[new(123,1,0)]);Check(Queue(policy,catalog)&&!Flush(writer),"first disk failure reported");
    Check(!File.Exists(Path.Combine(directory,"retry","portrait-owned-cache-v1.json")),"failed write cannot fabricate durable file");
    catalog.ApplyRead(owner,bag,now.AddSeconds(2),[new(123,1,0)]);Check(Queue(policy,catalog)&&Flush(writer),"same content retries on next observation");
    var restored=New();Check(file.Load(restored,out _),"retry publishes real cache");restored.ActivateCharacter(owner);Check(restored.BuildView(now).Items.Single().ItemID==123,"retry retained actual source content");
    Check(!Queue(policy,catalog)&&writer.CompletedWrites==2&&writer.FailedWrites==1,"successful retry suppresses further duplicate saves");
   });
   Case("burst changes bound pending work and dispose keeps newest state",()=>{
    var catalog=New();catalog.ActivateCharacter(owner);var file=new OwnedEquipmentCacheFile(Path.Combine(directory,"burst"));file.Load(catalog,out _);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var attempts=0;
    var writer=new OwnedCacheWriter(snapshot=>{if(Interlocked.Increment(ref attempts)==1){entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();}return file.SaveJSON(snapshot,out _);});var policy=Policy(writer);
    try{
     catalog.ApplyRead(owner,bag,now,[new(123,1,0)]);Check(Queue(policy,catalog)&&entered.Wait(TimeSpan.FromSeconds(5)),"initial burst snapshot is writing");
     var accepted=0;for(var i=1;i<=1000;i++){catalog.ApplyRead(owner,bag,now.AddSeconds(i),[new(123,1,0,(uint)i+1)]);if(Queue(policy,catalog))accepted++;}
     Check(accepted==1000,"all newer durable revisions accepted");release.Set();writer.Dispose();
     var restored=New();Check(file.Load(restored,out _),"disposed writer leaves readable newest file");restored.ActivateCharacter(owner);
     Check(restored.BuildView(now).Items.Single().Variants.Single().Quantity==1001&&writer.CompletedWrites==2,"one running plus one latest snapshot survives thousand updates");
    }finally{release.Set();writer.Dispose();}
   });
  }finally{
   var target=Path.GetFullPath(directory);var root=Path.GetFullPath(Path.GetTempPath());
   if(target.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("firegaze-owned-save-policy-",StringComparison.Ordinal)&&Directory.Exists(target))Directory.Delete(target,true);
  }
  return(pass,fail);
 }
}
