#if HAS_GROUNDED_MOTION
using FireGaze.LocalPreview.Runtime;

var cases = new (string, Action)[] {
    ("known on-foot source clips classify without guessing", Classifies),
    ("unknown flight swim and mounted source clips stay unknown", RejectsOtherModes),
    ("walk sustains without restarting and stop clears to idle", WalkStop),
    ("walk directions and turn changes issue one edge", Directions),
    ("run and sprint use the sampled known ground clip", RunSprint),
    ("jump stages change once and repeated landing never pins", JumpLanding),
    ("a later jump can play the same landing again", LaterJump),
    ("emote activity yields all animation writes", ActiveEmote),
    ("emote completion resumes the current locomotion clip", EmoteEnds),
    ("emote completion during landing emits one fresh edge", EmoteLanding),
    ("mounted idle uses rider mount pose without child access", MountedIdle),
    ("mounted motion is a rider plan and preserves ground clip", MountedMovement),
    ("mount replacement resets animation edge state", MountReplacement),
    ("dismount switches idle to on-foot release", Dismount),
    ("new owned actor receives fresh edge and old actor no release", NewActor),
    ("invalid sample releases only the planner owned override", InvalidSample),
    ("unknown sample never clears another writer override", ForeignOverride),
    ("invalid ownership never generates a write", InvalidOwner),
    ("reset forgets animation ownership without native cleanup", Reset),
};
int failed=0;
foreach (var (name,test) in cases) { try { test(); Console.WriteLine("PASS "+name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex.Message); } }
Console.WriteLine($"{cases.Length-failed}/{cases.Length} grounded motion planner cases passed");
return failed==0?0:1;

static void Eq<T>(T actual,T expected,string why) { if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"{why}: expected {expected}; got {actual}"); }
static GroundedMotionTarget Target(ushort mount=0,ulong id=1)=>new(id,mount);
static GroundedMotionSample Sample(ushort clip,bool valid=true)=>GroundedMotionClassifier.Classify(clip,valid);
static GroundedMotionPlan Plan(GroundedPreviewMotion driver,ushort clip,ushort current=0,bool emote=false,ushort mount=0,ulong id=1,bool valid=true)
    => driver.Plan(Sample(clip,valid),Target(mount,id),emote,current);
static void None(GroundedMotionPlan plan,string why) { Eq(plan.WriteBaseOverride,false,why+" override"); Eq(plan.PlayTimeline,(ushort)0,why+" playback"); }
static void Classifies() {
 foreach(ushort clip in new ushort[]{3,7,8,13,14,15,16,22,23,24,25,26,27,30,31,32,33,34,35,36,41,42,43,44,50,51,52,58,59,60,61}) {
  var sample=Sample(clip); Eq(sample.Known,true,"known source "+clip); Eq(sample.TimelineId,clip,"exact source clip");
 }
 Eq(Sample(3).Kind,GroundedMotionKind.Idle,"idle"); Eq(Sample(14).Kind,GroundedMotionKind.Walk,"walk"); Eq(Sample(31).Kind,GroundedMotionKind.Jump,"jump"); Eq(Sample(33).Kind,GroundedMotionKind.Landing,"landing");
}
static void RejectsOtherModes() { foreach(ushort clip in new ushort[]{0,1,2,4,71,165,166,167,4040,4051,4947,4968,5003,7367,8194,ushort.MaxValue})Eq(Sample(clip).Known,false,"unsupported source "+clip); Eq(Sample(13,false).Known,false,"invalid capture"); }
static void WalkStop() { var d=new GroundedPreviewMotion(); var first=Plan(d,13); Eq(first.PlayTimeline,(ushort)13,"walk edge"); Eq(first.BaseOverride,(ushort)13,"walk pin"); var held=Plan(d,13,13); Eq(held.WriteBaseOverride,true,"walk sustained"); Eq(held.PlayTimeline,(ushort)0,"no restart"); var stop=Plan(d,3,13); Eq(stop.WriteBaseOverride,true,"idle release"); Eq(stop.BaseOverride,(ushort)0,"natural idle"); Eq(stop.PlayTimeline,(ushort)0,"idle never replays"); None(Plan(d,3),"idle held"); }
static void Directions() { var d=new GroundedPreviewMotion(); foreach(ushort clip in new ushort[]{13,14,15,16,7,8}) { Eq(Plan(d,clip).PlayTimeline,clip,"direction edge"); Eq(Plan(d,clip,clip).PlayTimeline,(ushort)0,"direction sustained"); } }
static void RunSprint() { var d=new GroundedPreviewMotion(); foreach(ushort clip in new ushort[]{22,23,24,25,26,27,30,50,51,52,58})Eq(Plan(d,clip).PlayTimeline,clip,"ground sampled clip"); }
static void JumpLanding() { var d=new GroundedPreviewMotion(); Eq(Plan(d,31).PlayTimeline,(ushort)31,"takeoff edge"); Eq(Plan(d,31,31).PlayTimeline,(ushort)0,"takeoff held"); Eq(Plan(d,32,31).PlayTimeline,(ushort)32,"fall edge"); var land=Plan(d,33,32); Eq(land.PlayTimeline,(ushort)33,"landing edge"); Eq(land.WriteBaseOverride,true,"landing release"); Eq(land.BaseOverride,(ushort)0,"landing not pinned"); for(int i=0;i<15;i++)None(Plan(d,33),"held landing"); None(Plan(d,3),"landing natural idle"); }
static void LaterJump() { var d=new GroundedPreviewMotion(); Plan(d,33); Plan(d,3); Plan(d,31); Plan(d,32,31); Eq(Plan(d,33,32).PlayTimeline,(ushort)33,"new landing edge"); }
static void ActiveEmote() { var d=new GroundedPreviewMotion(); Plan(d,13); foreach(ushort clip in new ushort[]{13,22,31,0,33})None(Plan(d,clip,8194,true),"emote owns timeline"); }
static void EmoteEnds() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,13,13,true),"emote enters"); Eq(Plan(d,13,0).PlayTimeline,(ushort)13,"same motion restarts after emote"); Eq(Plan(d,13,13).PlayTimeline,(ushort)0,"only once"); }
static void EmoteLanding() { var d=new GroundedPreviewMotion(); Plan(d,32); None(Plan(d,33,8194,true),"emote suppresses landing"); Eq(Plan(d,33).PlayTimeline,(ushort)33,"landing upon actual resumption"); None(Plan(d,33),"no second landing"); }
static void MountedIdle() { var d=new GroundedPreviewMotion(); var idle=Plan(d,3,mount:446); Eq(idle.PlayTimeline,(ushort)166,"mounted rider idle"); Eq(idle.BaseOverride,(ushort)166,"mounted idle pin"); Eq(Plan(d,3,166,mount:446).PlayTimeline,(ushort)0,"no mounted idle restart"); }
static void MountedMovement() { var d=new GroundedPreviewMotion(); foreach(ushort clip in new ushort[]{13,22,7,31,32,33})Eq(Plan(d,clip,mount:446).PlayTimeline,clip,"rider ground candidate"); Eq(Plan(d,3,mount:446).PlayTimeline,(ushort)166,"mounted stop returns rider idle"); }
static void MountReplacement() { var d=new GroundedPreviewMotion(); Plan(d,22,mount:446); Eq(Plan(d,22,22,mount:447).PlayTimeline,(ushort)22,"new mount fresh edge"); Eq(Plan(d,22,22,mount:447).PlayTimeline,(ushort)0,"new mount held"); }
static void Dismount() { var d=new GroundedPreviewMotion(); Plan(d,3,mount:446); var p=Plan(d,3,166); Eq(p.WriteBaseOverride,true,"mounted pin release"); Eq(p.BaseOverride,(ushort)0,"onfoot idle"); Eq(p.PlayTimeline,(ushort)0,"no onfoot replay"); }
static void NewActor() { var d=new GroundedPreviewMotion(); Plan(d,13); var p=Plan(d,13,8194,id:2); Eq(p.PlayTimeline,(ushort)13,"new owner gets edge"); None(Plan(d,0,8194,id:3),"unknown new owner not cleared"); }
static void InvalidSample() { var d=new GroundedPreviewMotion(); Plan(d,13); var p=Plan(d,13,13,valid:false); Eq(p.WriteBaseOverride,true,"invalid capture release"); Eq(p.BaseOverride,(ushort)0,"release own pin"); Eq(p.PlayTimeline,(ushort)0,"invalid never starts motion"); None(Plan(d,0),"unknown repeated"); Eq(Plan(d,13).PlayTimeline,(ushort)13,"known capture recovers"); }
static void ForeignOverride() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,0,8194),"foreign pin preserved"); None(Plan(d,0,13),"ownership was released"); }
static void InvalidOwner() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,22,13,id:0),"no owned actor"); Eq(Plan(d,13,id:1).PlayTimeline,(ushort)13,"rebound starts fresh"); }
static void Reset() { var d=new GroundedPreviewMotion(); Plan(d,13); d.Reset(); None(Plan(d,0,13),"reset never touches old ownership"); Eq(Plan(d,13).PlayTimeline,(ushort)13,"reset fresh edge"); }
#else
Console.WriteLine("FAIL grounded motion planner: production GroundedPreviewMotion is absent");
return 1;
#endif
