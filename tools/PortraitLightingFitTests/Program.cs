#if HAS_LIGHTING_FIT
using FireGaze.PortraitPreview;
#endif

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string message) { checks++; if (!condition) failures.Add(message); }
#if HAS_LIGHTING_FIT
void Near(double actual, double expected, string message, double tolerance = 1e-6) =>
    Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, message + $" ({actual}, expected {expected})");

// A wrong channel curve, linear interpolation or endpoint handling fails these hand-derived values.
Check(PortraitColorToneTarget.TryCreate([32,128,224], [64,160,192], [64,128,192], out var curve),
    "three independent monotone channel targets can be created");
Check(curve!.TryMap([new(32,96,200)], out var mapped), "valid encoded RGB can be mapped");
Check(mapped!.SequenceEqual([new PortraitLightRgb(16,112,200)]),
    "red and green use their own monotone curve while blue remains identity");
Check(curve.TryMap([new(0,0,0),new(255,255,255)],out var ends)
    && ends!.SequenceEqual([new PortraitLightRgb(0,0,0),new(255,255,255)]), "curve endpoints remain fixed");
Check(!PortraitColorToneTarget.TryCreate([64,63,192],[64,128,192],[64,128,192],out _), "nonmonotone curve rejected");
Check(!PortraitColorToneTarget.TryCreate([-1,128,192],[64,128,192],[64,128,192],out _), "negative curve output rejected");
Check(!PortraitColorToneTarget.TryCreate([64,128,256],[64,128,192],[64,128,192],out _), "oversized curve output rejected");
Check(!PortraitColorToneTarget.TryCreate([64,128],[64,128,192],[64,128,192],out _), "unbounded control count rejected");
Check(!curve.TryMap([new(double.NaN,20,20)],out _), "nonfinite target pixel rejected");
Check(!curve.TryMap([new(256,20,20)],out _), "out-of-range target pixel rejected");

// Selection indices must remain fixed across every rendered frame, otherwise a false Jacobian results.
PortraitLightRgba[] selectionPixels = [new(10,20,30,255),new(50,60,70,255),new(20,20,20,0),new(80,90,100,255),new(110,120,130,255),new(1,1,1,255)];
bool[] mask = [false,true,true,true,true,false];
Check(PortraitLightPixelSelection.TryCreate(selectionPixels,mask,2,out var selection,out var selectionFailure), "masked representative selection succeeds");
Check(selectionFailure == PortraitLightingFitFailure.None && selection!.Indices.SequenceEqual([1,4]), "representatives are distributed across eligible pixels");
Check(selection!.TryExtract(selectionPixels,out var selected) && selected!.SequenceEqual([new PortraitLightRgb(50,60,70),new(110,120,130)]), "mask excludes frame/background and invisible alpha");
var changedAlpha=selectionPixels.ToArray(); changedAlpha[4]=changedAlpha[4] with { A=0 };
Check(!selection.TryExtract(changedAlpha,out _), "selected pixels becoming invisible invalidate a later sample");
Check(!selection.TryExtract(selectionPixels[..5],out _), "changed image dimensions reject a frame");
Check(!PortraitLightPixelSelection.TryCreate(selectionPixels,new bool[5],2,out _,out _), "mask shape mismatch rejected");
Check(!PortraitLightPixelSelection.TryCreate(selectionPixels,new bool[6],2,out _,out var noPixels)
    && noPixels==PortraitLightingFitFailure.NoPixels,"empty subject mask reports no pixels");
Check(!PortraitLightPixelSelection.TryCreate(selectionPixels,mask,4097,out _,out _), "sampling cannot exceed bounded workload");
var many=Enumerable.Repeat(new PortraitLightRgba(80,90,100,255),5000).ToArray();
Check(PortraitLightPixelSelection.TryCreate(many,Enumerable.Repeat(true,5000).ToArray(),4096,out var bounded,out _)
    && bounded!.Indices.Count==4096 && bounded.Indices.Distinct().Count()==4096,"4096 representatives are unique from a larger capture");

PortraitNativeLightRange[] Ranges(int quantum=1) => Enumerable.Repeat(new PortraitNativeLightRange(0,255,quantum),6).ToArray();
int[] Start() => [100,100,100,100,100,100];
// Two surfaces with different direct-vs-ambient response give six independent real color responses.
// Surface 1: RGB=50+directDelta; surface 2: RGB=100+ambientDelta.
PortraitLightRgb[] Render(IReadOnlyList<int> p) => [new(p[0]-50,p[1]-50,p[2]-50),new(p[3],p[4],p[5])];
PortraitLightSample Sample(IReadOnlyList<int> p) => new(p.ToArray(),Render(p));
PortraitLightFitInput Input(PortraitLightRgb[]? target=null,PortraitNativeLightRange[]? ranges=null,int probeStep=10)
{
    var start=Start(); var probes=new List<PortraitLightSample>();
    for(var axis=0;axis<6;axis++) {var p=start.ToArray(); p[axis]+=probeStep; probes.Add(Sample(p));}
    return new(Sample(start),ranges??Ranges(),probes,target??[new(55,55,55),new(105,105,105)]);
}
bool Fit(PortraitLightFitInput input,out PortraitLightFitProposal? proposal,out PortraitLightingFitFailure failure,
    PortraitLightFitOptions? options=null)=>PortraitLightingFit.TryPropose(input,options??new(),out proposal,out failure);

// A sign error, response normalization bug or RGB/axis mismatch prevents the exact six-channel target.
Check(Fit(Input(),out var proposal,out var fitFailure), "a calibrated independent lighting response yields a proposal");
Check(fitFailure==PortraitLightingFitFailure.None && proposal!.CandidateParameters.SequenceEqual([105,105,105,105,105,105]), "six independent channel parameters recover the hand-derived target");
Near(proposal!.BaselineRootMeanSquareError,5,"baseline error measured against target");
Near(proposal.PredictedRootMeanSquareError,0,"quantized linear prediction matches exact target");
var accepted=proposal.ValidateRenderedCandidate(Sample(proposal.CandidateParameters));
Check(accepted.Accepted && accepted.Failure==PortraitLightingFitFailure.None,"actual re-rendered pixels must pass the acceptance gate");
Near(accepted.RootMeanSquareError,0,"verified candidate measures actual pixels");

// Predicted success is insufficient: nonlinear response, wrong native fields, or stale capture may be worse.
var worse=new PortraitLightSample(proposal.CandidateParameters.ToArray(),[new(40,40,40),new(90,90,90)]);
var rejected=proposal.ValidateRenderedCandidate(worse);
Check(!rejected.Accepted && rejected.Failure==PortraitLightingFitFailure.ActualImprovementInsufficient,"actual worse output rejected despite a perfect prediction");
Check(!proposal.ValidateRenderedCandidate(new(Start(),Render(Start()))).Accepted,"candidate parameter identity must match proposed quantized settings");
Check(!proposal.ValidateRenderedCandidate(new(proposal.CandidateParameters.ToArray(),[new(55,55,55)])).Accepted,"candidate capture must have matching pixel count");
Check(!proposal.ValidateRenderedCandidate(new(proposal.CandidateParameters.ToArray(),[new(double.NaN,0,0),new(0,0,0)])).Accepted,"nonfinite candidate rejected");
var tinyActual=new PortraitLightSample(proposal.CandidateParameters.ToArray(),[new(50.1,50.1,50.1),new(100.1,100.1,100.1)]);
Check(!proposal.ValidateRenderedCandidate(tinyActual).Accepted,"improvement below absolute threshold is not accepted");
var relativeOptions = new PortraitLightFitOptions(MinimumAbsoluteImprovement:0,MinimumRelativeImprovement:.1);
Check(Fit(Input(),out var relativeProposal,out _,relativeOptions),"relative-threshold fixture produces a proposal");
Check(!relativeProposal!.ValidateRenderedCandidate(new(relativeProposal.CandidateParameters.ToArray(),[new(50.2,50.2,50.2),new(100.2,100.2,100.2)])).Accepted,
    "actual four-percent improvement fails a ten-percent relative requirement");

var positive=Input();
var signedProbes=positive.AxisProbes.Select((_,axis)=> {var p=Start();p[axis]-=10;return Sample(p);}).ToArray();
Check(Fit(positive with {AxisProbes=signedProbes},out var signed,out _) && signed!.CandidateParameters.SequenceEqual([105,105,105,105,105,105]),"negative one-sided probes preserve Jacobian sign");
var mixedTarget=new PortraitLightRgb[]{new(45,60,42),new(110,95,103)};
Check(Fit(Input(mixedTarget),out var mixed,out _) && mixed!.CandidateParameters.SequenceEqual([95,110,92,110,95,103]),"independent positive and negative per-channel changes stay separate");
var quantizedRanges=Enumerable.Repeat(new PortraitNativeLightRange(0,250,5),6).ToArray();
Check(Fit(Input([new(57,57,57),new(107,107,107)],quantizedRanges),out var quantized,out _)
    && quantized!.CandidateParameters.SequenceEqual([105,105,105,105,105,105]),"candidate uses nearest legal native quantum");
var quantizedActual=quantized!.ValidateRenderedCandidate(Sample(quantized.CandidateParameters));
Check(quantizedActual.Accepted,"partial native-quantized improvement can be accepted");
Near(quantizedActual.RootMeanSquareError,2,"unattainable curve retains measured residual error");
Check(Fit(Input([new(150,150,150),new(200,200,200)]),out var trusted,out _)
    && trusted!.CandidateParameters.All(v=>v==130),"fit limits extrapolation to measured probe multiples");
var boundedRanges=Enumerable.Repeat(new PortraitNativeLightRange(0,110,1),6).ToArray();
Check(Fit(Input([new(80,80,80),new(130,130,130)],boundedRanges),out var clipped,out _)
    && clipped!.CandidateParameters.All(v=>v==110),"native range wins over desired unattainable target");

var malformed=Input();
Check(!Fit(malformed with {TargetPixels=[new(0,0,0)]},out _,out var wrongCount)
    && wrongCount==PortraitLightingFitFailure.InvalidInput,"target sample length mismatch rejected");
Check(!Fit(malformed with {TargetPixels=[new(256,0,0),new(0,0,0)]},out _,out _),"invalid RGB target rejected");
Check(!Fit(malformed with {Ranges=Ranges()[..5]},out _,out _),"exactly six native RGB controls required");
var invalidRanges=Ranges();invalidRanges[1]=new(0,255,0);
Check(!Fit(malformed with {Ranges=invalidRanges},out _,out _),"zero quantum rejected");
invalidRanges=Ranges();invalidRanges[1]=new(0,255,2);
Check(!Fit(malformed with {Ranges=invalidRanges},out _,out _),"nonintegral native quantization endpoints rejected");
invalidRanges=Ranges();invalidRanges[1]=new(101,255,1);
Check(!Fit(malformed with {Ranges=invalidRanges},out _,out _),"baseline out of native range rejected");
Check(!Fit(malformed with {AxisProbes=malformed.AxisProbes.Take(5).ToArray()},out _,out _),"incomplete six-axis calibration rejected");
var duplicate=malformed.AxisProbes.ToArray(); duplicate[5]=duplicate[4];
Check(!Fit(malformed with {AxisProbes=duplicate},out _,out _),"duplicate axis cannot masquerade as full calibration");
var twoAxis=malformed.AxisProbes.ToArray();var invalid=Start();invalid[0]+=10;invalid[1]+=10;twoAxis[0]=Sample(invalid);
Check(!Fit(malformed with {AxisProbes=twoAxis},out _,out _),"sample touching two native controls rejected");
var unchanged=malformed.AxisProbes.ToArray();unchanged[0]=Sample(Start());
Check(!Fit(malformed with {AxisProbes=unchanged},out _,out _),"unchanged native parameter cannot calibrate an axis");
var shortProbe=malformed.AxisProbes.ToArray();shortProbe[1]=new(shortProbe[1].Parameters,[new(50,50,50)]);
Check(!Fit(malformed with {AxisProbes=shortProbe},out _,out _),"probe capture dimension mismatch rejected");
var nanProbe=malformed.AxisProbes.ToArray();nanProbe[1]=new(nanProbe[1].Parameters,[new(50,double.NaN,50),new(100,100,100)]);
Check(!Fit(malformed with {AxisProbes=nanProbe},out _,out _),"nonfinite probe rejected before solver");
var inert=malformed.AxisProbes.Select(p=>new PortraitLightSample(p.Parameters,malformed.Baseline.Pixels)).ToArray();
Check(!Fit(malformed with {AxisProbes=inert},out _,out var inertFailure)
    && inertFailure==PortraitLightingFitFailure.InsufficientResponse,"zero rendered response rejects calibration");
var dependent=malformed.AxisProbes.Select(p=>new PortraitLightSample(p.Parameters,[new(60,60,60),new(110,110,110)])).ToArray();
Check(!Fit(malformed with {AxisProbes=dependent},out _,out var degenerate)
    && degenerate==PortraitLightingFitFailure.DegenerateResponse,"indistinguishable six light responses rejected rather than amplified");
Check(!Fit(Input([new(50,50,50),new(100,100,100)]),out _,out var identityFailure)
    && identityFailure==PortraitLightingFitFailure.NoTargetChange,"identity target does not produce a fake successful edit");
Check(!Fit(malformed,out _,out _,new PortraitLightFitOptions(Regularization:double.NaN)),"invalid solver settings rejected");
Check(!Fit(malformed,out _,out _,new PortraitLightFitOptions(MaximumProbeExtrapolation:.5)),"invalid trust radius rejected");
Check(!Fit(malformed,out _,out _,new PortraitLightFitOptions(MinimumRelativeImprovement:1.1)),"impossible acceptance threshold rejected");
// Removing regularization during native-grid refinement would incorrectly turn a deliberately
// motion-penalized zero-step solution into a full five-step edit.
Check(!Fit(Input(),out _,out var regularizedFailure,new PortraitLightFitOptions(Regularization:1000000))
    && regularizedFailure==PortraitLightingFitFailure.PredictedImprovementInsufficient,
    "native-grid refinement preserves the least-squares motion penalty");
var manySamples=Enumerable.Repeat(new PortraitLightRgb(50,50,50),4097).ToArray();
Check(!Fit(malformed with {Baseline=new(Start(),manySamples),TargetPixels=manySamples},out _,out _),"solver independently bounds its pixel workload");

// Proposal must own calibration snapshots; reuse of a caller buffer cannot change later acceptance.
var mutableInput=Input();
var ownedTarget=(PortraitLightRgb[])mutableInput.TargetPixels;
var ownedBaseline=(PortraitLightRgb[])mutableInput.Baseline.Pixels;
Check(Fit(mutableInput,out var immutable,out _),"mutable caller arrays can calibrate");
ownedTarget[0]=new(0,0,0);ownedBaseline[0]=new(255,255,255);
var immutableActual=immutable!.ValidateRenderedCandidate(Sample(immutable.CandidateParameters));
Check(immutableActual.Accepted,"post-fit caller mutation cannot rewrite verification target or baseline");
Near(immutableActual.BaselineRootMeanSquareError,5,"proposal retains original baseline error");

// Direct and ambient light really cross-couple on surfaces: this is not six independent brightness sliders.
PortraitLightRgb[] CrossRender(IReadOnlyList<int> p) => [
    new(50+p[0]-100+.5*(p[3]-100),50+p[1]-100+.5*(p[4]-100),50+p[2]-100+.5*(p[5]-100)),
    new(100+.5*(p[0]-100)+p[3]-100,100+.5*(p[1]-100)+p[4]-100,100+.5*(p[2]-100)+p[5]-100)];
PortraitLightSample CrossSample(IReadOnlyList<int> p) => new(p.ToArray(),CrossRender(p));
var crossProbes=Enumerable.Range(0,6).Select(axis=> {var p=Start();p[axis]+=10;return CrossSample(p);}).ToArray();
var crossInput=new PortraitLightFitInput(CrossSample(Start()),Ranges(),crossProbes,[new(51,48.5,60),new(94.5,101.5,108)]);
Check(Fit(crossInput,out var cross,out _) && cross!.CandidateParameters.SequenceEqual([105,97,108,92,103,104]),
    "correlated main and ambient lighting recovers the independently derived six-parameter solution");
Check(cross!.ValidateRenderedCandidate(CrossSample(cross.CandidateParameters)).Accepted,"cross-coupled candidate still requires real-render verification");
PortraitLightRgb[] NearDependentRender(IReadOnlyList<int> p) => [
    new(50+p[0]-100+p[3]-100,50+p[1]-100+p[4]-100,50+p[2]-100+p[5]-100),
    new(100+p[0]-100+1.000001*(p[3]-100),100+p[1]-100+1.000001*(p[4]-100),100+p[2]-100+1.000001*(p[5]-100))];
var nearDependent=crossProbes.Select(probe=>new PortraitLightSample(probe.Parameters,NearDependentRender(probe.Parameters))).ToArray();
Check(!Fit(crossInput with {AxisProbes=nearDependent},out _,out var nearDegenerate)
    && nearDegenerate==PortraitLightingFitFailure.DegenerateResponse,
    "almost indistinguishable finite responses are rejected before solving");

#else
Check(false,"the constrained rendered-lighting fitter has not been implemented");
#endif
Console.WriteLine($"PortraitLightingFitTests: {checks-failures.Count} passed, {failures.Count} failed");
foreach(var failure in failures) Console.Error.WriteLine("FAIL: "+failure);
return failures.Count==0?0:1;
