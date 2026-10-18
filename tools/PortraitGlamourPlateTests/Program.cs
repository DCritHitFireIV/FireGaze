using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game;

var passed=0;var failed=0;
void Check(bool ok,string name){if(ok)passed++;else{failed++;Console.WriteLine($"FAIL {name}");}}
void Case(string name,Action test){try{test();}catch(Exception error){failed++;Console.WriteLine($"FAIL {name}: {error.GetType().Name} {error.Message}");}}
var owner=new OwnedCharacterKey(1,101);var other=new OwnedCharacterKey(1,202);var now=DateTimeOffset.Parse("2026-10-19T00:00:00Z");
PortraitGlamourPlateCatalog Catalog()=>new();
PortraitGlamourPlateCache Cache(string directory)=>new(directory);
PortraitGlamourPlateReader Reader(Func<bool> framework,Func<OwnedCharacterKey?> character,Func<nint> manager)=>new(framework,character,manager);
PortraitGlamourPlate[] Pages(uint first=123,uint last=234)
{
 var pages=new PortraitGlamourPlate[20];
 for(var i=0;i<20;i++){var pieces=new PortraitPiece[12];if(i==0&&first!=0)pieces[2]=new(first,4,5);if(i==19&&last!=0)pieces[11]=new(last,6,7);pages[i]=new(i,pieces);}
 return pages;
}
PortraitGlamourPlate Page(int index,PortraitPiece[] pieces)=>new(index,pieces);
bool Merge(PortraitLook current,PortraitGlamourPlate plate,out PortraitLook merged,out string reason)
 =>PortraitGlamourPlateMerge.TryMerge(current,plate,out merged,out reason);

Case("complete template merge clears empty slots and preserves facewear and display flags",()=>{
 var current=new PortraitLook(Enumerable.Range(0,13).Select(i=>new PortraitPiece((uint)i+1,1,2)).ToArray(),false,true,true);
 var pieces=new PortraitPiece[12];pieces[2]=new(123,4,5);var plate=Page(3,pieces);
 Check(Merge(current,plate,out var merged,out _),"nonempty complete plate merges");
 Check(merged.Pieces[0]==default&&merged.Pieces[2]==new PortraitPiece(123,4,5)&&merged.Pieces[11]==default,"twelve slots represent full replacement including cleared slots");
 Check(merged.Pieces[12]==current.Pieces[12]&&!merged.HeadVisible&&merged.WeaponVisible&&merged.Visor,"facewear and original visibility survive");
 pieces[2]=default;merged.Pieces[12]=default;Check(current.Pieces[12].ItemID==13&&current.Pieces[2].ItemID==3,"merge output and source arrays are detached");
 Check(!Merge(current,Page(0,new PortraitPiece[12]),out _,out var emptyReason)&&emptyReason.Length>0,"empty saved template remains non-applicable");
 Check(!Merge(current,Page(20,new PortraitPiece[12]),out _,out _),"out of range plate index rejected");
 var invalid=new PortraitPiece[12];invalid[0]=new(0,1,0);Check(!Merge(current,Page(0,invalid),out _,out _),"malformed empty dye cannot enter portrait");
 Check(!Merge(current,Page(0,new PortraitPiece[11]),out _,out _),"partial template rejected by merger");
 Check(!Merge(current with{Pieces=new PortraitPiece[12]},plate,out _,out _),"portrait without facewear slot rejected without mutation");
});
Case("only complete successful source reads create content revisions",()=>{
 var catalog=Catalog();catalog.ActivateCharacter(owner);catalog.MarkUnavailable(owner,"not loaded");
 Check(catalog.ContentRevision==0&&catalog.BuildView(now).Status==OwnedReadStatus.Unread,"unread source is not an authoritative empty template list");
 var pages=Pages();Check(catalog.ApplyRead(owner,now,pages),"twenty complete templates accepted");
 Check(catalog.ContentRevision==1&&catalog.BuildView(now).Plates.Count==20,"first complete set is durable");
 catalog.ApplyRead(owner,now.AddSeconds(2),Pages());Check(catalog.ContentRevision==1&&catalog.BuildView(now.AddSeconds(2)).ReadAt==now.AddSeconds(2),"repeat read updates time without persistence churn");
 catalog.MarkUnavailable(owner,"zone changed");Check(catalog.ContentRevision==1&&catalog.BuildView(now).Status==OwnedReadStatus.PossiblyStale&&catalog.BuildView(now).Plates[19].Pieces[11].ItemID==234,"unavailable source retains last complete set");
 var partial=new PortraitGlamourPlate[19];Check(!catalog.ApplyRead(owner,now.AddSeconds(4),partial)&&catalog.ContentRevision==1,"partial set cannot clear valid cached templates");
 Check(!catalog.ApplyRead(owner,now.AddSeconds(1),Pages(345))&&catalog.ContentRevision==1,"older callback cannot replace templates");
 Check(!catalog.ApplyRead(owner,now.AddSeconds(2),Pages(345))&&catalog.ContentRevision==1,"same-time conflict rejected");
 Check(catalog.ApplyRead(owner,now.AddSeconds(4),Pages(0,0))&&catalog.ContentRevision==2&&catalog.BuildView(now).Plates.Count==20,"authoritative all-empty set replaces old complete set");
 var duplicate=Pages();duplicate[19]=duplicate[0];Check(!catalog.ApplyRead(owner,now.AddSeconds(6),duplicate)&&catalog.ContentRevision==2,"duplicate template indices cannot masquerade as a complete set");
 var malformed=Pages();malformed[19].Pieces[0]=new(0,1,0);Check(!catalog.ApplyRead(owner,now.AddSeconds(6),malformed)&&catalog.ContentRevision==2,"malformed saved dye state rejected before changing content");
 Check(catalog.ApplyRead(owner,now.AddSeconds(6),Pages().Reverse().ToArray())&&catalog.BuildView(now).Plates[19].Index==19,"complete sets normalize source order");
 Check(catalog.BuildView(now.AddMinutes(20)).Status==OwnedReadStatus.PossiblyStale,"old observations retain contents with stale status");
});
Case("template cache isolates character identity and detached data",()=>{
 var catalog=Catalog();catalog.ActivateCharacter(owner);var pages=Pages();catalog.ApplyRead(owner,now,pages);
 pages[0].Pieces[2]=default(PortraitPiece);var view=catalog.BuildView(now);Check(view.Plates[0].Pieces[2].ItemID==123,"caller cannot mutate accepted cache");
 view.Plates[0].Pieces[2]=default(PortraitPiece);Check(catalog.BuildView(now).Plates[0].Pieces[2].ItemID==123,"view cannot mutate internal cache");
 catalog.ActivateCharacter(other);Check(catalog.BuildView(now).Status==OwnedReadStatus.Unread,"another character does not inherit ownership");
 Check(!catalog.ApplyRead(owner,now.AddSeconds(2),Pages(345)),"late owner callback rejected");
 catalog.ApplyRead(other,now,Pages(345));catalog.ActivateCharacter(new OwnedCharacterKey(99,101));Check(catalog.BuildView(now).Plates[0].Pieces[2].ItemID==123,"stable content identity survives account token changes");
 var json=catalog.ExportJSON();var copy=Catalog();Check(copy.ImportJSON(json),"portable cache roundtrip");copy.ActivateCharacter(owner);
 Check(copy.ContentRevision==0&&copy.BuildView(now).Status==OwnedReadStatus.PossiblyStale,"loaded baseline is stale and not newly dirty");
 Check(!copy.ImportJSON("{bad")&&copy.BuildView(now).Plates[0].Pieces[2].ItemID==123,"invalid import preserves prior cache");
 var badVersion=JsonNode.Parse(json)!;badVersion["Version"]=99;Check(!copy.ImportJSON(badVersion.ToJsonString()),"unknown cache schema refused");
 var duplicateOwner=JsonNode.Parse(json)!;var owners=duplicateOwner["Characters"]!.AsArray();owners.Add(owners[0]!.DeepClone());Check(!copy.ImportJSON(duplicateOwner.ToJsonString()),"duplicate content identity refused");
 var missing=JsonNode.Parse(json)!;missing["Characters"]![0]!["Plates"]!.AsArray().RemoveAt(19);Check(!copy.ImportJSON(missing.ToJsonString())&&copy.BuildView(now).Plates[0].Pieces[2].ItemID==123,"partial persisted templates cannot erase valid observations");
 catalog.DeactivateCharacter();Check(catalog.BuildView(now).Character is null&&!catalog.ApplyRead(owner,now.AddSeconds(4),Pages()),"logout clears active identity and rejects late reads");
 try{catalog.ActivateCharacter(new(0,101));Check(false,"zero account rejected");}catch(ArgumentException){Check(true,"zero account rejected");}
});
Case("cache writes immediately and preserves corrupt originals",()=>{
 var directory=Path.Combine(Path.GetTempPath(),"firegaze-glamour-plate-"+Guid.NewGuid().ToString("N"));
 try{
  var catalog=Catalog();catalog.ActivateCharacter(owner);var cache=Cache(directory);
  try{
   string reason;Check(!cache.Load(catalog,out reason),"new local cache begins without a file");catalog.ApplyRead(owner,now,Pages());
   Check(cache.QueueIfNeeded(catalog)&&cache.FlushAsync().GetAwaiter().GetResult(),"accepted read is durable before close");
   var count=(int)cache.CompletedWrites;catalog.ApplyRead(owner,now.AddSeconds(2),Pages());Check(!cache.QueueIfNeeded(catalog)&&cache.CompletedWrites==count,"identical refresh does not rewrite file");
   catalog.ApplyRead(owner,now.AddSeconds(3),Pages(456));Check(cache.QueueIfNeeded(catalog)&&cache.FlushAsync().GetAwaiter().GetResult(),"changed content atomically replaces an existing cache");
   var copy=Catalog();var reader=Cache(directory);try{Check(reader.Load(copy,out reason),"written primary loads independently");copy.ActivateCharacter(owner);Check(copy.BuildView(now).Plates[0].Pieces[2].ItemID==456&&copy.BuildView(now).Plates[19].Pieces[11].Stain1==7,"atomic replacement and last template dual dye survive independent load");}finally{reader.Dispose();}
  }finally{cache.Dispose();}
  var path=Path.Combine(directory,"portrait-glamour-plates-v1.json");File.WriteAllText(path,"broken");var corrupt=Cache(directory);
  try{string reason;Check(!corrupt.Load(catalog,out reason),"corrupt cache is refused");catalog.ApplyRead(owner,now.AddSeconds(4),Pages(345));Check(!corrupt.QueueIfNeeded(catalog)&&File.ReadAllText(path)=="broken","invalid primary cannot be overwritten by fresh observations");}finally{corrupt.Dispose();}
 }finally{
  var target=Path.GetFullPath(directory);var root=Path.GetFullPath(Path.GetTempPath());if(target.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("firegaze-glamour-plate-",StringComparison.Ordinal)&&Directory.Exists(target))Directory.Delete(target,true);
 }
});
Case("failed disk write retries the same unchanged content revision",()=>{
 var directory=Path.Combine(Path.GetTempPath(),"firegaze-glamour-plate-"+Guid.NewGuid().ToString("N"));
 try{
  File.WriteAllText(directory,"block directory creation");var catalog=Catalog();catalog.ActivateCharacter(owner);catalog.ApplyRead(owner,now,Pages());
  using var cache=Cache(directory);
  Check(cache.QueueIfNeeded(catalog)&&!cache.FlushAsync().GetAwaiter().GetResult()&&cache.FailedWrites==1,"write failure is observable and leaves the revision dirty");
  File.Delete(directory);Directory.CreateDirectory(directory);
  Check(cache.QueueIfNeeded(catalog)&&cache.FlushAsync().GetAwaiter().GetResult()&&cache.CompletedWrites==2,"same revision succeeds after local storage recovers");
  var copy=Catalog();using var reader=Cache(directory);Check(reader.Load(copy,out _),"retry is durable before plugin disposal");copy.ActivateCharacter(owner);
  Check(copy.BuildView(now).Plates[19].Pieces[11].Stain1==7,"recovered file preserves complete template set");
 }finally{
  var target=Path.GetFullPath(directory);var root=Path.GetFullPath(Path.GetTempPath());
  if(target.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("firegaze-glamour-plate-",StringComparison.Ordinal)){
   if(Directory.Exists(target))Directory.Delete(target,true);else if(File.Exists(target))File.Delete(target);
  }
 }
});
unsafe{
 var memory=(MirageManager*)NativeMemory.AllocZeroed((nuint)sizeof(MirageManager));
 try{
  var address=(nint)memory;
  Case("native reader is readonly loaded-gated and owner-bound",()=>{
   var reader=Reader(()=>true,()=>owner,()=>address);Check(reader.Capture().Plates is null,"unloaded manager does not return empty plates");
   memory->GlamourPlatesLoaded=true;memory->GlamourPlates[0].ItemIds[2]=1000123;memory->GlamourPlates[0].Stain0Ids[2]=4;memory->GlamourPlates[0].Stain1Ids[2]=5;
   memory->GlamourPlates[19].ItemIds[11]=234;memory->GlamourPlates[19].Stain1Ids[11]=7;
   var before=new ReadOnlySpan<byte>(memory,sizeof(MirageManager)).ToArray();var capture=reader.Capture();
   Check(capture.Character==owner&&capture.Plates?.Count==20&&capture.Plates[0].Pieces[2].ItemID==123&&capture.Plates[19].Pieces[11].Stain1==7,"twenty saved templates and twelve slots copied with quality normalization");
   Check(new ReadOnlySpan<byte>(memory,sizeof(MirageManager)).SequenceEqual(before),"source memory remains byte-for-byte unchanged");
   memory->IsApplyingGlamourPlate=true;Check(reader.Capture().Plates is null,"in-progress actual application is not cached");memory->IsApplyingGlamourPlate=false;
   var reads=0;var swapped=Reader(()=>true,()=>++reads==1?owner:other,()=>address);Check(swapped.Capture().Plates is null,"character change discards detached snapshot");
   reads=0;var unloaded=Reader(()=>true,()=>{if(++reads==2)memory->GlamourPlatesLoaded=false;return owner;},()=>address);Check(unloaded.Capture().Plates is null,"unloaded-after-copy snapshot is discarded");
   var factoryCalls=0;var worker=Reader(()=>false,()=>owner,()=>{factoryCalls++;return address;});try{worker.Capture();Check(false,"worker-thread access rejected");}catch(InvalidOperationException){Check(factoryCalls==0,"thread guard runs before native resolver");}
   var signedOutCalls=0;var signedOut=Reader(()=>true,()=>null,()=>{signedOutCalls++;return address;});Check(signedOut.Capture().Plates is null&&signedOutCalls==0,"logged-out reader does not resolve native manager");
   memory->GlamourPlatesLoaded=true;memory->GlamourPlates[0].ItemIds[2]=0;var cleared=reader.Capture();Check(cleared.Plates![0].Pieces[2]==default,"zero native item normalizes stale dye bytes to a clear slot");
   var pointerReads=0;var changingPointer=Reader(()=>true,()=>owner,()=>++pointerReads==1?address:0);Check(changingPointer.Capture().Plates is null,"manager replacement during copy discards snapshot");
  });
 }finally{NativeMemory.Free(memory);}
}
Console.WriteLine($"PortraitGlamourPlateTests: {passed} passed, {failed} failed; game calls: 0");Environment.ExitCode=failed==0?0:1;
