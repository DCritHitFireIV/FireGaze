using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FireGaze.PortraitPreview;

internal static unsafe class NativeReadTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string n) { if(ok)pass++; else{fail++;Console.WriteLine($"FAIL native-layout {n}");} }
  var items=(InventoryItem*)NativeMemory.AllocZeroed((nuint)(sizeof(InventoryItem)*2));
  var container=(InventoryContainer*)NativeMemory.AllocZeroed((nuint)sizeof(InventoryContainer));
  try {
   container->Type=InventoryType.Inventory1; container->Size=2; container->Items=items;
   var source=new OwnedSourceKey(OwnedSourceKind.Inventory);
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out var read,out _),"unloaded not authoritative empty");
   container->IsLoaded=true;
   items[0].Container=InventoryType.Inventory1;items[0].Slot=0;items[0].ItemId=123;items[0].Quantity=2;items[0].Stains[0]=3;items[0].Stains[1]=4;items[0].GlamourId=567;items[0].Flags=InventoryItem.ItemFlags.HighQuality;
   items[1].Container=InventoryType.Inventory1;items[1].Slot=1;
   Check(OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out read,out _),"loaded accepted");
   Check(read.Count==1 && read[0].ItemID==123 && read[0].Stain1==3 && read[0].Stain2==4 && read[0].GlamourID==567 && read[0].HighQuality && read[0].Quantity==2,"fields read from installed layout");
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container,0],out read,out _) && read.Count==0,"partial group not committed");
   container->Type=InventoryType.RetainerMarket;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"market source excluded");
   container->Type=InventoryType.Inventory1;items[0].IsSymbolic=true;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"symbolic slot rejected not misowned");
   items[0].IsSymbolic=false;items[0].Container=InventoryType.RetainerPage1;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"slot container mismatch rejected");
   items[0].Container=InventoryType.Inventory1;items[0].Slot=1;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"slot index mismatch rejected");
   items[0].Slot=0;container->Size=-1;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"negative size rejected");
   container->Size=100_001;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"unbounded size rejected");
   container->Size=2;items[0].Quantity=-1;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"invalid quantity rejected");
   items[0].Quantity=2;container->Items=null;
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out _,out _),"missing items rejected");
   container->Items=items;items[0].ItemId=0;items[0].Quantity=0;
   Check(OwnedNativeInventoryReader.TryReadLoadedContainers(source,[(nint)container],out read,out _) && read.Count==0,"loaded empty explicit");
   Check(!OwnedNativeInventoryReader.TryReadLoadedContainers(source,[],out _,out _),"zero containers not authoritative");
  } finally {NativeMemory.Free(container);NativeMemory.Free(items);}
  var cache=(ItemFinderRetainerInventory*)NativeMemory.AllocZeroed((nuint)sizeof(ItemFinderRetainerInventory));
  try {
   cache->ItemIds[0]=123;cache->ItemCount[0]=2;cache->EquippedItemIds[0]=234;
   Check(OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(123,10000,0,2)],(nint)cache),"retainer keyed bag cache matches");
   Check(OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerEquipped,[new(234,11000,0)],(nint)cache),"retainer keyed equipped cache matches");
   Check(!OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(123,10000,0,1)],(nint)cache),"retainer quantity mismatch rejected");
   Check(!OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(234,10000,0,2)],(nint)cache),"prior retainer buffer rejected");
   Check(!OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(123,10000,0,2)],0),"missing keyed cache rejected");
   Check(!OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.Inventory,[new(123,0,0,2)],(nint)cache),"non retainer source rejected");
   cache->ItemIds[0]=1000123;
   Check(OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(123,10000,0,2,HighQuality:true)],(nint)cache),"HQ encoded keyed cache ownership matches");
   cache->ItemIds[1]=123;cache->ItemCount[1]=1;
   Check(OwnedNativeInventoryReader.MatchesRetainerCache(OwnedSourceKind.RetainerInventory,[new(123,10000,0,2),new(123,10000,1)],(nint)cache),"split stacks compared by total");
  } finally {NativeMemory.Free(cache);}
  Console.WriteLine($"Installed-layout memory checks: {pass} passed, {fail} failed; not an in-game acceptance.");
  return(pass,fail);
 }
}
