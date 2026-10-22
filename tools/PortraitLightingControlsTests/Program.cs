using System.Numerics;
using FireGaze.PortraitPreview;
var passed=0; var failed=0;
void Check(bool result,string label){Console.WriteLine((result?"PASS ":"FAIL ")+label);if(result)passed++;else failed++;}
#if CONTROLS
var model=new PortraitLightingFitControls();
Check(model.TryTarget(out var identity)&&identity!.TryMap([new(64,128,192)],out var pixels)&&pixels![0]==new PortraitLightRgb(64,128,192),"initial target preserves all RGB channels");
model.SetPoint(0,1,500);
Check(model.GetPoints(0).SequenceEqual(new[]{64,192,192}),"middle point cannot cross highlights");
model.SetPoint(0,0,-40);
Check(model.GetPoints(0).SequenceEqual(new[]{0,192,192}),"shadow output stays in game color range");
model.SetPoint(0,2,3);
Check(model.GetPoints(0).SequenceEqual(new[]{0,192,192}),"highlight cannot cross middle");
Check(model.GetPoints(1).SequenceEqual(new[]{64,128,192}),"red editing leaves green unchanged");
var copy=model.GetPoints(0).ToArray();copy[0]=33;
Check(model.GetPoints(0)[0]==0,"published controls cannot mutate stored target");
model.SetPoint(-1,0,20);model.SetPoint(4,0,20);model.SetPoint(0,9,20);
Check(model.GetPoints(0)[0]==0,"invalid channel or point cannot alter target");
model.BindSource("one");
Check(model.SelectRegion(new(.7f,.9f),new(.1f,.2f))&&model.Region==new PortraitSampleRegion(.1f,.2f,.7f,.9f),"reversed drag selects explicit normalized subject");
model.BindSource("one");
Check(model.Region is not null,"same editor retains selection");
model.BindSource("two");
Check(model.Region is null,"new editor requires a fresh selection");
Check(!model.SelectRegion(new(-1,0),new(1,1))&&model.Region is null,"out-of-image selection is refused");
model.ResetCurves();
Check(Enumerable.Range(0,3).All(c=>model.GetPoints(c).SequenceEqual(new[]{64,128,192})),"reset restores all channels");
Check(model.TryTarget(out var target)&&target!.TryMap([new(20,130,250)],out var mapped)&&mapped![0]==new PortraitLightRgb(20,130,250),"reset creates an identity goal");
#else
Check(false,"real lighting control model is missing");
#endif
Console.WriteLine($"{passed} passed, {failed} failed");return failed==0?0:1;
