using System.Collections.Concurrent;
using FireGaze.PortraitPreview;

internal static class TrackedWriterTests
{
 public static (int Passed,int Failed) Run()
 {
  int pass=0,fail=0;
  void Check(bool ok,string name){if(ok)pass++;else{fail++;Console.WriteLine($"FAIL tracked-writer {name}");}}
  void Case(string name,Action test){try{test();}catch(Exception e){fail++;Console.WriteLine($"FAIL tracked-writer {name}: {e.GetType().Name} {e.Message}");}}
  bool Queue(OwnedCacheWriter writer,long revision,string snapshot)=>writer.Queue(revision,snapshot);
  bool Needs(OwnedCacheWriter writer,long revision)=>writer.NeedsRevision(revision);
  long Saved(OwnedCacheWriter writer)=>writer.SavedRevision;
  long Outstanding(OwnedCacheWriter writer)=>writer.OutstandingRevision;
  Task<bool> Flush(OwnedCacheWriter writer)=>writer.FlushAsync();
  Case("flush waits for newest accepted snapshot and saved revision never regresses",()=>{
   using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var output=new ConcurrentQueue<string>();
   var writer=new OwnedCacheWriter(snapshot=>{if(snapshot=="first"){entered.Set();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();}output.Enqueue(snapshot);return true;});
   try{
    Check(Queue(writer,1,"first"),"first tracked save accepted");Check(entered.Wait(TimeSpan.FromSeconds(5)),"first tracked save running");
    Check(Queue(writer,2,"obsolete")&&Queue(writer,3,"latest"),"newer revisions replace pending snapshot");
    Check(Saved(writer)==0&&Outstanding(writer)==3,"unsaved work is observable before completion");
    Check(!Queue(writer,2,"stale")&&!Needs(writer,3),"stale and duplicate outstanding revisions cannot replace latest content");
    var draining=Flush(writer);Check(!draining.IsCompleted,"flush does not acknowledge pending bytes");release.Set();
    Check(draining.Wait(TimeSpan.FromSeconds(5))&&draining.Result,"flush acknowledges latest durable result");
    Check(output.SequenceEqual(new[]{"first","latest"})&&writer.CompletedWrites==2,"intermediate revisions coalesce");
    Check(Saved(writer)==3&&Outstanding(writer)==0&&!Needs(writer,3),"saved revision advances only after persistence");
    Check(!Queue(writer,2,"old")&&!Queue(writer,3,"duplicate"),"completed revisions cannot regress persisted state");
   }finally{release.Set();writer.Dispose();}
  });
  Case("same revision may retry after returned or thrown write failure",()=>{
   var attempts=0;using var writer=new OwnedCacheWriter(_=>{var n=Interlocked.Increment(ref attempts);if(n==1)return false;if(n==2)throw new IOException("disk");return true;});
   Check(Queue(writer,4,"content"),"initial save accepted");Check(!Flush(writer).GetAwaiter().GetResult(),"returned failure is not acknowledged");
   Check(Saved(writer)==0&&Outstanding(writer)==0&&Needs(writer,4),"failed revision remains eligible");
   Check(!Queue(writer,3,"obsolete")&&!Needs(writer,3),"an older revision cannot replace the retained latest failure");
   Check(Queue(writer,4,"content"),"same revision accepted after returned failure");Check(!Flush(writer).GetAwaiter().GetResult(),"thrown failure is not acknowledged");
   Check(Queue(writer,4,"content")&&Flush(writer).GetAwaiter().GetResult(),"same revision retry eventually persists");
   Check(Saved(writer)==4&&writer.CompletedWrites==3&&writer.FailedWrites==2,"saved and failed attempts tracked independently");
   writer.Dispose();Check(!Queue(writer,5,"late")&&!Needs(writer,5),"disposed writer does not accept new persistence work");
  });
  return(pass,fail);
 }
}
