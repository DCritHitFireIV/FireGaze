using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game;

internal static class RetainerGateTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string name){if(ok)pass++;else{fail++;Console.WriteLine($"FAIL retainer-gate {name}");}}
  var alice=new OwnedCharacterKey(11,101);var bob=new OwnedCharacterKey(11,202);
  var bagA=new OwnedSourceKey(OwnedSourceKind.RetainerInventory,501);var bagB=new OwnedSourceKey(OwnedSourceKind.RetainerInventory,502);
  var equipA=new OwnedSourceKey(OwnedSourceKind.RetainerEquipped,501);
  var all=new Dictionary<uint,bool>();for(uint i=10000;i<=10006;i++)all[i]=true;all[11000]=true;
  var unloaded=all.ToDictionary(pair=>pair.Key,_=>false);
  var gate=new OwnedRetainerReadGate();
  Check(!gate.CanRead(bagA),"no initial evidence");gate.Observe(alice,501,all);
  Check(!gate.CanRead(bagA) && !gate.CanRead(equipA),"already-loaded old buffer not trusted");
  gate.Observe(alice,501,unloaded);Check(!gate.CanRead(bagA),"unloaded not readable");gate.Observe(alice,501,all);
  Check(gate.CanRead(bagA) && gate.CanRead(equipA),"each container explicit reload grants current owner");
  Check(!gate.CanRead(bob,bagA),"previous frame proof cannot authorize another current character");
  Check(gate.CanRead(alice,bagA),"current identity can use its matching proof");
  Check(!gate.CanRead(bagB),"proof not shared across retainers");
  gate.Observe(alice,502,all);Check(!gate.CanRead(bagB),"switch with same item counts and old dyes rejected");
  gate.Observe(alice,502,unloaded);var partial=all.ToDictionary(pair=>pair.Key,pair=>pair.Value);partial[10006]=false;
  gate.Observe(alice,502,partial);Check(!gate.CanRead(bagB),"all seven pages required");
  gate.Observe(alice,502,all);Check(gate.CanRead(bagB),"new owner fully reloaded can read");
  gate.Observe(alice,501,all);Check(!gate.CanRead(bagA),"returning previously seen owner requires new epoch proof");
  var onlyBagsUnloaded=all.ToDictionary(pair=>pair.Key,pair=>pair.Key==11000);gate.Observe(alice,501,onlyBagsUnloaded);gate.Observe(alice,501,all);
  Check(gate.CanRead(bagA) && !gate.CanRead(equipA),"bags proof cannot grant equipped old dye metadata");
  gate.Observe(bob,501,all);Check(!gate.CanRead(bagA),"character change clears all owner proof");
  gate.Observe(alice,501,unloaded);gate.Observe(alice,501,all);Check(gate.CanRead(bagA),"new character epoch can establish proof again");
  gate.Observe(null,0,all);Check(!gate.CanRead(bagA),"logout clears proof");
  gate.Observe(alice,501,unloaded);gate.Observe(alice,501,all);
  var missing=all.Where(pair=>pair.Key!=10003).ToDictionary();gate.Observe(alice,501,missing);
  Check(!gate.CanRead(bagA),"missing loaded observation cannot remain readable");
  gate.Observe(alice,501,all);Check(gate.CanRead(bagA),"explicit previous false remains proof after subsequent full load");
  Check(!gate.CanRead(new(OwnedSourceKind.Inventory)),"player inventory cannot consume retainer proof");
  return(pass,fail);
 }
}
