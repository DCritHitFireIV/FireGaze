using System.Numerics;
#if HAS_REGION
using FireGaze.PortraitPreview;
#endif
var passed = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL " + name); passed++; }
#if !HAS_REGION
Check(false, "a dragged subject rectangle selects only its own source pixels");
#else
Check(PortraitSampleRegion.TryFromDrag(new(.25f,.25f),new(.75f,.75f),out var region)
    && region.TryMask(16,16,out var mask) && mask.Count(x=>x)==64, "center rectangle selects exactly 64 source pixel centers");
region.TryMask(16,16,out var selected);
Check(selected[4*16+4] && selected[11*16+11] && !selected[3*16+4] && !selected[12*16+11], "ROI boundaries exclude adjacent background pixels");
Check(PortraitSampleRegion.TryFromDrag(new(.75f,.75f),new(.25f,.25f),out var reversed)
    && reversed==region, "reverse-direction pointer gesture retains identical subject bounds");
foreach(var value in new[]{Vector2.Zero,new Vector2(float.NaN,.5f),new Vector2(float.PositiveInfinity,.5f),new Vector2(-.1f,.5f),new Vector2(1.1f,.5f)})
    Check(!PortraitSampleRegion.TryFromDrag(value,value,out _),"invalid or zero-area gesture cannot select a region");
Check(!PortraitSampleRegion.TryFromDrag(new(-.01f,.1f),new(.8f,.9f),out _),"outside start cannot silently include unselected pixels");
Check(!PortraitSampleRegion.TryFromDrag(new(.01f,.1f),new(1.01f,.9f),out _),"outside end cannot silently include unselected pixels");
Check(!region.TryMask(8,8,out _) && !region.TryMask(0,16,out _) && !region.TryMask(int.MaxValue,16,out _),"insufficient or oversized calibration regions are rejected before allocation");
Check(!default(PortraitSampleRegion).TryMask(16,16,out _),"unset region cannot sample the whole background by default");
Check(PortraitSampleRegion.TryFromDrag(Vector2.Zero,Vector2.One,out var all)
    && all.TryMask(16,16,out var full) && full.All(x=>x), "explicit full-frame choice includes every pixel");
#endif
Console.WriteLine($"PortraitSampleRegionTests: {passed} passed, 0 failed");
