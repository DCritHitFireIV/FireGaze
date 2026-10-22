#if HAS_GROUNDED_MOTION
using FireGaze.LocalPreview.Runtime;

var cases = new (string, Action)[] {
    ("known on-foot source clips classify without guessing", Classifies),
    ("unknown flight swim and mounted source clips stay unknown", RejectsOtherModes),
    ("standing cpose start and loop clips require explicit standing evidence", StandingPoseClips),
    ("walk sustains without restarting and stop clears to idle", WalkStop),
    ("walk directions and turn changes issue one edge", Directions),
    ("run and sprint use the sampled known ground clip", RunSprint),
    ("jump stages change once and repeated landing never pins", JumpLanding),
    ("a later jump can play the same landing again", LaterJump),
    ("emote activity yields all animation writes", ActiveEmote),
    ("emote completion resumes the current locomotion clip", EmoteEnds),
    ("emote completion during landing emits one fresh edge", EmoteLanding),
    ("mounted idle independently plans rider and mount", MountedIdle),
    ("mounted rider keeps mounted pose during ground locomotion", MountedMovement),
    ("mounted child receives canonical ground clips and independent edges", MountedChild),
    ("mounted landing releases child pin and never repeats", MountedLanding),
    ("child identity or generation change starts fresh lanes", ChildReplacement),
    ("unknown mounted sample preserves each foreign override", MountedUnknown),
    ("mounted emote yields both lanes and resumes fresh", MountedEmote),
    ("mount replacement resets animation edge state", MountReplacement),
    ("dismount switches idle to on-foot release", Dismount),
    ("new owned actor receives fresh edge and old actor no release", NewActor),
    ("invalid sample releases only the planner owned override", InvalidSample),
    ("unknown sample never clears another writer override", ForeignOverride),
    ("invalid ownership never generates a write", InvalidOwner),
    ("reset forgets animation ownership without native cleanup", Reset),
    ("flight appearance selects only owned visual rider and child loops", FlightAppearance),
    ("flight appearance toggle refreshes both lane edges", FlightToggleEdges),
    ("flight appearance never infers physics from unknown source clips", FlightUnknown),
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
static void None(GroundedMotionPlan plan,string why) { Eq(plan.WriteBaseOverride,false,why+" override"); Eq(plan.PlayTimeline,(ushort)0,why+" playback"); Eq(plan.WriteMountBaseOverride,false,why+" mount override"); Eq(plan.PlayMountTimeline,(ushort)0,why+" mount playback"); }
static void Classifies() {
 foreach(ushort clip in new ushort[]{3,7,8,13,14,15,16,22,23,24,25,26,27,30,31,32,33,34,35,36,41,42,43,44,50,51,52,58,59,60,61}) {
  var sample=Sample(clip); Eq(sample.Known,true,"known source "+clip); Eq(sample.TimelineId,clip,"exact source clip");
 }
 Eq(Sample(3).Kind,GroundedMotionKind.Idle,"idle"); Eq(Sample(14).Kind,GroundedMotionKind.Walk,"walk"); Eq(Sample(31).Kind,GroundedMotionKind.Jump,"jump"); Eq(Sample(33).Kind,GroundedMotionKind.Landing,"landing");
}
static void RejectsOtherModes() { foreach(ushort clip in new ushort[]{0,1,2,4,71,165,166,167,4040,4051,4947,4968,5003,7367,8194,ushort.MaxValue})Eq(Sample(clip).Known,false,"unsupported source "+clip); Eq(Sample(13,false).Known,false,"invalid capture"); }
static void StandingPoseClips() { foreach(ushort clip in new ushort[]{1052,1053,1054,1055,3123,3124,3125,3126,3181,3182,3183,3184,7404,7405,7406,7407,13330,13331,13332,13333}) { Eq(GroundedMotionClassifier.Classify(clip).Known,false,"pose without standing evidence "+clip); var confirmed=GroundedMotionClassifier.Classify(clip,true,true); Eq(confirmed.Known,true,"confirmed standing pose "+clip); Eq(confirmed.Kind,GroundedMotionKind.Idle,"standing pose is stationary "+clip); } Eq(GroundedMotionClassifier.Classify(164,true,true).Known,false,"teleport remains unknown with pose metadata");Eq(GroundedMotionClassifier.Classify(7404,false,true).Known,false,"invalid source never becomes standing idle"); }
static void WalkStop() { var d=new GroundedPreviewMotion(); var first=Plan(d,13); Eq(first.PlayTimeline,(ushort)13,"walk edge"); Eq(first.BaseOverride,(ushort)13,"walk pin"); var held=Plan(d,13,13); Eq(held.WriteBaseOverride,true,"walk sustained"); Eq(held.PlayTimeline,(ushort)0,"no restart"); var stop=Plan(d,3,13); Eq(stop.WriteBaseOverride,true,"idle release"); Eq(stop.BaseOverride,(ushort)0,"natural idle"); Eq(stop.PlayTimeline,(ushort)0,"idle never replays"); None(Plan(d,3),"idle held"); }
static void Directions() { var d=new GroundedPreviewMotion(); foreach(ushort clip in new ushort[]{13,14,15,16,7,8}) { Eq(Plan(d,clip).PlayTimeline,clip,"direction edge"); Eq(Plan(d,clip,clip).PlayTimeline,(ushort)0,"direction sustained"); } }
static void RunSprint() { var d=new GroundedPreviewMotion(); foreach(ushort clip in new ushort[]{22,23,24,25,26,27,30,50,51,52,58})Eq(Plan(d,clip).PlayTimeline,clip,"ground sampled clip"); }
static void JumpLanding() { var d=new GroundedPreviewMotion(); Eq(Plan(d,31).PlayTimeline,(ushort)31,"takeoff edge"); Eq(Plan(d,31,31).PlayTimeline,(ushort)0,"takeoff held"); Eq(Plan(d,32,31).PlayTimeline,(ushort)32,"fall edge"); var land=Plan(d,33,32); Eq(land.PlayTimeline,(ushort)33,"landing edge"); Eq(land.WriteBaseOverride,true,"landing release"); Eq(land.BaseOverride,(ushort)0,"landing not pinned"); for(int i=0;i<15;i++)None(Plan(d,33),"held landing"); None(Plan(d,3),"landing natural idle"); }
static void LaterJump() { var d=new GroundedPreviewMotion(); Plan(d,33); Plan(d,3); Plan(d,31); Plan(d,32,31); Eq(Plan(d,33,32).PlayTimeline,(ushort)33,"new landing edge"); }
static void ActiveEmote() { var d=new GroundedPreviewMotion(); Plan(d,13); foreach(ushort clip in new ushort[]{13,22,31,0,33})None(Plan(d,clip,8194,true),"emote owns timeline"); }
static void EmoteEnds() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,13,13,true),"emote enters"); Eq(Plan(d,13,0).PlayTimeline,(ushort)13,"same motion restarts after emote"); Eq(Plan(d,13,13).PlayTimeline,(ushort)0,"only once"); }
static void EmoteLanding() { var d=new GroundedPreviewMotion(); Plan(d,32); None(Plan(d,33,8194,true),"emote suppresses landing"); Eq(Plan(d,33).PlayTimeline,(ushort)33,"landing upon actual resumption"); None(Plan(d,33),"no second landing"); }
static void MountedIdle() { var d=new GroundedPreviewMotion(); var idle=Plan(d,3,mount:446); Eq(idle.PlayTimeline,(ushort)166,"mounted rider idle"); Eq(idle.BaseOverride,(ushort)166,"mounted idle pin"); Eq(idle.PlayMountTimeline,(ushort)3,"mount idle edge"); Eq(idle.MountBaseOverride,(ushort)3,"mount idle pin"); Eq(Plan(d,3,166,mount:446).PlayTimeline,(ushort)0,"no mounted idle restart"); }
static void MountedMovement() { var d=new GroundedPreviewMotion(); foreach(var (source,rider,edge) in new (ushort,ushort,ushort)[]{(13,6709,6709),(22,167,167),(7,9306,9306),(8,9307,9307),(31,6710,6710),(32,6710,0),(33,166,166),(3,166,0)}) { var p=Plan(d,source,mount:446); Eq(p.BaseOverride,rider,"ground mounted pose for "+source); Eq(p.PlayTimeline,edge,"mounted pose edge for "+source); } }
static void MountedChild() { var d=new GroundedPreviewMotion(); foreach(var (source,child,rider) in new (ushort,ushort,ushort)[]{(41,13,6709),(42,14,6709),(12004,15,6709),(12005,16,6709),(50,22,167),(12009,23,167),(12010,24,167),(25,22,167),(30,22,167),(35,3,9306),(12007,3,9307)}) { var p=Plan(d,source,mount:73); Eq(p.MountBaseOverride,child,"canonical mount pin for "+source); Eq(p.BaseOverride,rider,"separate rider pose for "+source); } var held=Plan(d,12007,mount:73);Eq(held.PlayMountTimeline,(ushort)0,"same mount loop not restarted");Eq(held.PlayTimeline,(ushort)0,"same rider loop not restarted"); }
static void MountedLanding() { var d=new GroundedPreviewMotion();Plan(d,31,mount:73);Plan(d,32,mount:73);var land=Plan(d,33,mount:73);Eq(land.PlayMountTimeline,(ushort)33,"mount landing edge");Eq(land.MountBaseOverride,(ushort)0,"mount landing not held");Eq(land.BaseOverride,(ushort)166,"rider returns mounted idle");for(int i=0;i<15;i++){var held=Plan(d,33,mount:73);Eq(held.PlayMountTimeline,(ushort)0,"no repeated mount landing");Eq(held.WriteMountBaseOverride,false,"no landing pin");}var idle=Plan(d,3,mount:73);Eq(idle.PlayMountTimeline,(ushort)3,"mount idle after landing");Eq(idle.PlayTimeline,(ushort)0,"rider idle not replayed"); }
static void ChildReplacement() { var d=new GroundedPreviewMotion();d.Plan(Sample(22),new(1,73,100,4),false,0,0);foreach(var target in new GroundedMotionTarget[]{new(1,73,101,4),new(1,73,101,5)}){var fresh=d.Plan(Sample(22),target,false,167,22);Eq(fresh.PlayTimeline,(ushort)167,"new mount requires fresh rider edge");Eq(fresh.PlayMountTimeline,(ushort)22,"new child requires fresh ground edge");var held=d.Plan(Sample(22),target,false,167,22);Eq(held.PlayTimeline,(ushort)0,"new rider held");Eq(held.PlayMountTimeline,(ushort)0,"new child held");} }
static void MountedUnknown() { var d=new GroundedPreviewMotion();var target=new GroundedMotionTarget(1,73,100,4);d.Plan(Sample(13),target,false,0,0);var release=d.Plan(Sample(164),target,false,6709,13);Eq(release.WriteBaseOverride,true,"release own rider");Eq(release.BaseOverride,(ushort)0,"rider release value");Eq(release.WriteMountBaseOverride,true,"release own child");Eq(release.MountBaseOverride,(ushort)0,"child release value");d.Plan(Sample(13),target,false,0,0);None(d.Plan(Sample(164),target,false,8194,999),"foreign lane writers"); }
static void MountedEmote() { var d=new GroundedPreviewMotion();var target=new GroundedMotionTarget(1,73,100,4);d.Plan(Sample(13),target,false,0,0);None(d.Plan(Sample(22),target,true,8194,999),"emote owns rider and mount");var resume=d.Plan(Sample(13),target,false,0,0);Eq(resume.PlayTimeline,(ushort)6709,"resume rider");Eq(resume.PlayMountTimeline,(ushort)13,"resume mount"); }
static void MountReplacement() { var d=new GroundedPreviewMotion(); Plan(d,22,mount:446); Eq(Plan(d,22,167,mount:447).PlayTimeline,(ushort)167,"new mount fresh edge"); Eq(Plan(d,22,167,mount:447).PlayTimeline,(ushort)0,"new mount held"); }
static void Dismount() { var d=new GroundedPreviewMotion(); Plan(d,3,mount:446); var p=Plan(d,3,166); Eq(p.WriteBaseOverride,true,"mounted pin release"); Eq(p.BaseOverride,(ushort)0,"onfoot idle"); Eq(p.PlayTimeline,(ushort)0,"no onfoot replay"); }
static void NewActor() { var d=new GroundedPreviewMotion(); Plan(d,13); var p=Plan(d,13,8194,id:2); Eq(p.PlayTimeline,(ushort)13,"new owner gets edge"); None(Plan(d,0,8194,id:3),"unknown new owner not cleared"); }
static void InvalidSample() { var d=new GroundedPreviewMotion(); Plan(d,13); var p=Plan(d,13,13,valid:false); Eq(p.WriteBaseOverride,true,"invalid capture release"); Eq(p.BaseOverride,(ushort)0,"release own pin"); Eq(p.PlayTimeline,(ushort)0,"invalid never starts motion"); None(Plan(d,0),"unknown repeated"); Eq(Plan(d,13).PlayTimeline,(ushort)13,"known capture recovers"); }
static void ForeignOverride() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,0,8194),"foreign pin preserved"); None(Plan(d,0,13),"ownership was released"); }
static void InvalidOwner() { var d=new GroundedPreviewMotion(); Plan(d,13); None(Plan(d,22,13,id:0),"no owned actor"); Eq(Plan(d,13,id:1).PlayTimeline,(ushort)13,"rebound starts fresh"); }
static void Reset() { var d=new GroundedPreviewMotion(); Plan(d,13); d.Reset(); None(Plan(d,0,13),"reset never touches old ownership"); Eq(Plan(d,13).PlayTimeline,(ushort)13,"reset fresh edge"); }
static GroundedMotionTarget FlightTarget(bool active=true,ushort mount=327) {
 var property=typeof(GroundedMotionTarget).GetProperty("FlightPose");if(property is null)throw new Exception("flight appearance flag missing");
 object target=new GroundedMotionTarget(1,mount,100,4);property.SetValue(target,active);return (GroundedMotionTarget)target;
}
static void FlightAppearance() {
 foreach(var (source,rider,child) in new (ushort,ushort,ushort)[]{(3,4047,4040),(13,4048,4053),(14,4048,4053),(15,4048,4053),(16,4048,4046),(22,4048,4043),(23,4048,4043),(24,4048,4043),(25,4048,4043),(30,4048,4043),(7,4047,4040),(8,4047,4040),(31,4047,4040),(32,4047,4040),(33,4047,4040)}) {
  var d=new GroundedPreviewMotion();var p=d.Plan(Sample(source),FlightTarget(),false,0,0);Eq(p.PlayTimeline,rider,"visual rider "+source);Eq(p.PlayMountTimeline,child,"visual mount "+source);Eq(p.MountBaseOverride,child,"visual loop held "+source);
 }
 var foot=new GroundedPreviewMotion().Plan(Sample(13),FlightTarget(true,0),false,0,0);Eq(foot.PlayTimeline,(ushort)13,"flight flag has no onfoot effect");Eq(foot.PlayMountTimeline,(ushort)0,"no onfoot child");
}
static void FlightToggleEdges(){var d=new GroundedPreviewMotion();var p=d.Plan(Sample(13),FlightTarget(),false,0,0);Eq(p.PlayTimeline,(ushort)4048,"first flight edge");var held=d.Plan(Sample(13),FlightTarget(),false,4048,4053);Eq(held.PlayTimeline,(ushort)0,"flight not restarted");Eq(held.PlayMountTimeline,(ushort)0,"flight child not restarted");var ground=d.Plan(Sample(13),FlightTarget(false),false,4048,4053);Eq(ground.PlayTimeline,(ushort)6709,"ground rider resumes");Eq(ground.PlayMountTimeline,(ushort)13,"ground child resumes");var again=d.Plan(Sample(13),FlightTarget(),false,6709,13);Eq(again.PlayTimeline,(ushort)4048,"flight rider resumes");Eq(again.PlayMountTimeline,(ushort)4053,"flight child resumes");}
static void FlightUnknown(){var d=new GroundedPreviewMotion();d.Plan(Sample(22),FlightTarget(),false,0,0);var unknown=d.Plan(Sample(4043),FlightTarget(),false,4048,4043);Eq(unknown.PlayTimeline,(ushort)0,"real flight source never interpreted");Eq(unknown.PlayMountTimeline,(ushort)0,"no flight child inferred");Eq(unknown.BaseOverride,(ushort)0,"own rider pin released");Eq(unknown.MountBaseOverride,(ushort)0,"own mount pin released");None(d.Plan(Sample(13),FlightTarget(),true,8194,999),"special action owns both lanes");}
#else
Console.WriteLine("FAIL grounded motion planner: production GroundedPreviewMotion is absent");
return 1;
#endif
