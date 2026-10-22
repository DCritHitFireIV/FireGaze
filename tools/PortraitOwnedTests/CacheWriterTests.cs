using System.Collections.Concurrent;
using FireGaze.PortraitPreview;

internal static class CacheWriterTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string name){if(ok)pass++;else{fail++;Console.WriteLine($"FAIL cache-writer {name}");}}
  void Case(string name,Action test){try{test();}catch(Exception e){fail++;Console.WriteLine($"FAIL cache-writer {name}: {e.GetType().Name} {e.Message}");}}
  Case("newest pending snapshot replaces obsolete saves and dispose drains",()=>{
   var output=new ConcurrentQueue<string>();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
   var callerThread=Environment.CurrentManagedThreadId;var ranOffCaller=false;
   var writer=new OwnedCacheWriter(snapshot=>{if(snapshot=="first"){ranOffCaller=Environment.CurrentManagedThreadId!=callerThread;entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();}output.Enqueue(snapshot);return true;});
   try{
    Check(writer.Queue("first"),"first accepted");Check(entered.Wait(TimeSpan.FromSeconds(5)),"worker started");
    Check(writer.Queue("second") && writer.Queue("third"),"queued without waiting for disk");
    var disposing=Task.Run(writer.Dispose);
    Check(!disposing.Wait(50),"dispose waits while pending disk write blocked");release.Set();
    Check(disposing.Wait(TimeSpan.FromSeconds(5)),"dispose completes after drain");
    Check(output.SequenceEqual(new[]{"first","third"}),"only running and newest pending snapshots reach disk");
    Check(ranOffCaller,"writer does disk work on worker");Check(writer.CompletedWrites==2 && writer.FailedWrites==0,"completed status counts actual writes");
    Check(!writer.Queue("after-dispose"),"closed writer rejects new work");writer.Dispose();
   }finally{release.Set();writer.Dispose();}
  });
  Case("failure return and exceptions do not strand following saves",()=>{
   var output=new ConcurrentQueue<string>();using var writer=new OwnedCacheWriter(snapshot=>{if(snapshot=="throws")throw new IOException("simulated disk failure");if(snapshot=="false")return false;output.Enqueue(snapshot);return true;});
   writer.Queue("throws");Check(SpinWait.SpinUntil(()=>writer.CompletedWrites==1,TimeSpan.FromSeconds(5)),"thrown failure completed");
   writer.Queue("false");Check(SpinWait.SpinUntil(()=>writer.CompletedWrites==2,TimeSpan.FromSeconds(5)),"returned failure completed");
   writer.Queue("final");writer.Dispose();
   Check(output.SequenceEqual(new[]{"final"}),"final save runs after earlier failures");
   Check(writer.CompletedWrites==3 && writer.FailedWrites==2,"failure totals include returned and thrown failures");
   Check(writer.LastWriteSucceeded==true,"last status reflects successful final write");
  });
  Case("empty requests rejected before disk boundary",()=>{
   var output=new ConcurrentQueue<string>();using var writer=new OwnedCacheWriter(snapshot=>{output.Enqueue(snapshot);return true;});
   Check(!writer.Queue("") && !writer.Queue("   "),"empty snapshot rejected");writer.Dispose();Check(output.IsEmpty,"invalid requests never reach disk");
  });
  return(pass,fail);
 }
}
