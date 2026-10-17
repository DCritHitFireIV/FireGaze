using FireGaze.PortraitPreview;

internal static class CacheFileTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string n) {if(ok)pass++;else{fail++;Console.WriteLine($"FAIL cache-file {n}");}}
  var directory=Path.Combine(Path.GetTempPath(),"firegaze-portrait-owned-test-"+Guid.NewGuid().ToString("N"));
  try {
   var character=new OwnedCharacterKey(1,2);var source=new OwnedSourceKey(OwnedSourceKind.Inventory);
   var c=new OwnedEquipmentCatalog(itemID=>itemID==123 ? itemID : null);c.ActivateCharacter(character);c.ApplyRead(character,source,DateTimeOffset.UtcNow,[new(123,0,0,1,4,5)]);
   var file=new OwnedEquipmentCacheFile(directory);
   Check(!file.Load(c,out _) && c.BuildView(DateTimeOffset.UtcNow).Items.Count==1,"absent file preserves observations");
   Check(file.Save(c,out _),"save succeeds");
   var restored=new OwnedEquipmentCatalog(itemID=>itemID==123 ? itemID : null);restored.ActivateCharacter(character);
   Check(file.Load(restored,out _),"load succeeds");
   var view=restored.BuildView(DateTimeOffset.UtcNow);
   Check(view.Items.Single().Variants.Single().Stain2==5,"cache file retains dual dyes");
   Check(view.Sources.Single().Status==OwnedReadStatus.PossiblyStale,"file imported stale");
   Check(!file.SaveJSON("not jsonText",out _) && file.Load(restored,out _),"invalid worker snapshot cannot overwrite valid cache");
   var detachedJSON=c.ExportJSON();
   Check(Task.Run(()=>file.SaveJSON(detachedJSON,out _)).GetAwaiter().GetResult(),"detached snapshot can save from worker");
   File.WriteAllText(Path.Combine(directory,"portrait-owned-cache-v1.json"),"not jsonText");
   Check(!file.Load(restored,out _) && restored.BuildView(DateTimeOffset.UtcNow).Items.Count==1,"invalid file preserves prior observations");
   Check(Directory.EnumerateFiles(directory).Count()==1,"temporary files cleaned");
  } catch(Exception e) {fail++;Console.WriteLine($"FAIL cache-file exception {e.Message}");}
  finally {
   var target=Path.GetFullPath(directory);var tempRoot=Path.GetFullPath(Path.GetTempPath());
   if(target.StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("firegaze-portrait-owned-test-",StringComparison.Ordinal) && Directory.Exists(target))Directory.Delete(target,true);
  }
  return(pass,fail);
 }
}
