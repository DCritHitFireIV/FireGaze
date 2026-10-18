using System.Numerics;
#if HAS_IMAGE_ANALYSIS || HAS_LIGHT_DIRECTION
using FireGaze.PortraitPreview;
#endif

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string message) { checks++; if (!condition) failures.Add(message); }
#if HAS_IMAGE_ANALYSIS || HAS_LIGHT_DIRECTION
void Near(double actual, double expected, string message, double tolerance = 0.00001) =>
    Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, message + $" ({actual}, expected {expected})");
#endif

#if HAS_IMAGE_ANALYSIS
// Each fixture has hand-derived channel/luma bins. Swapping RGBA/BGRA or counting transparent padding breaks these.
byte[] rgbFixture = [255,0,0,255, 0,255,0,255, 0,0,255,255, 128,128,128,255, 255,255,255,0];
var original = rgbFixture.ToArray();
var analysis = PortraitImageAnalysis.Analyze(rgbFixture, 5, 1, 20, PortraitPixelOrder.Rgba);
Check(analysis.VisiblePixels == 4 && analysis.IgnoredPixels == 1, "transparent pixels do not enter the sample denominator");
Check(analysis.Red[255] == 1 && analysis.Red[0] == 2 && analysis.Red[128] == 1, "red histogram uses RGBA red channel");
Check(analysis.Green[255] == 1 && analysis.Green[0] == 2 && analysis.Blue[255] == 1, "green and blue channels remain independent");
Check(analysis.Luma[54] == 1 && analysis.Luma[182] == 1 && analysis.Luma[18] == 1 && analysis.Luma[128] == 1,
    "display luma bins use encoded Rec.709 weights, not linear-light exposure values");
Check(analysis.Luma.Sum() == 4 && analysis.Red.Sum() == 4 && analysis.Green.Sum() == 4 && analysis.Blue.Sum() == 4,
    "all histograms have the same visible pixel count");
Check(rgbFixture.SequenceEqual(original), "analysis never modifies caller pixels");
var bgra = PortraitImageAnalysis.Analyze([0,0,255,255, 255,0,0,255],2,1,8,PortraitPixelOrder.Bgra);
Check(bgra.Red[255] == 1 && bgra.Blue[255] == 1 && bgra.Luma[54] == 1 && bgra.Luma[18] == 1,
    "BGRA order maps to the same semantic colors");
var padded = PortraitImageAnalysis.Analyze([10,10,10,255, 255,255,255,255, 20,20,20,255],1,2,8,PortraitPixelOrder.Rgba);
Check(padded.VisiblePixels == 2 && padded.Luma[10] == 1 && padded.Luma[20] == 1 && padded.Luma[255] == 0,
    "stride padding is skipped and last row need not include trailing padding");

byte[] clippingPixels = [0,0,0,255, 10,10,10,255, 245,245,245,255, 255,255,255,255, 255,0,0,255, 255,255,255,0];
var clipping = PortraitImageAnalysis.Analyze(clippingPixels,6,1,24,PortraitPixelOrder.Rgba,
    new PortraitAnalysisOptions(BlackThreshold:10, WhiteThreshold:245));
Near(clipping.BlackClipFraction, .4, "black clipping threshold is inclusive and excludes alpha zero");
Near(clipping.WhiteClipFraction, .4, "white clipping threshold is inclusive without treating red as white");
Near(clipping.RedHighClipFraction, .6, "single-channel highlights report saturated red separately");
Near(clipping.GreenHighClipFraction, .4, "green channel clipping uses visible denominator");
Near(clipping.BlueHighClipFraction, .4, "blue channel clipping uses visible denominator");
var ends = PortraitImageAnalysis.Analyze(clippingPixels,6,1,24,PortraitPixelOrder.Rgba);
Near(ends.BlackClipFraction,.2,"default black threshold means exact display zero");
Near(ends.WhiteClipFraction,.2,"default white threshold means exact display white");
var empty = PortraitImageAnalysis.Analyze([255,255,255,0],1,1,4,PortraitPixelOrder.Rgba);
Check(empty.VisiblePixels == 0 && empty.IgnoredPixels == 1 && empty.Luma.Sum() == 0, "empty visible image keeps zero counts");
Near(empty.BlackClipFraction,0,"empty image black ratio avoids NaN");
Near(empty.WhiteClipFraction,0,"empty image white ratio avoids NaN");
Check(empty.Hotspots.Count == 0 && empty.Attention.All(value => value == 0), "transparent image never fabricates a hotspot");
var alphaCut = PortraitImageAnalysis.Analyze([5,5,5,1, 100,100,100,128],2,1,8,PortraitPixelOrder.Rgba,
    new PortraitAnalysisOptions(MinimumAlpha:128));
Check(alphaCut.VisiblePixels == 1 && alphaCut.IgnoredPixels == 1 && alphaCut.Luma[100] == 1,
    "explicit minimum alpha is inclusive");
var premul = PortraitImageAnalysis.Analyze([64,32,0,128],1,1,4,PortraitPixelOrder.Rgba,
    new PortraitAnalysisOptions(AlphaMode:PortraitPixelAlpha.Premultiplied));
Check(premul.Red[128] == 1 && premul.Green[64] == 1 && premul.Blue[0] == 1,
    "premultiplied channels are unpremultiplied before histogramming");
var bgraPremul = PortraitImageAnalysis.Analyze([0,32,64,128],1,1,4,PortraitPixelOrder.Bgra,
    new PortraitAnalysisOptions(AlphaMode:PortraitPixelAlpha.Premultiplied));
Check(bgraPremul.Red.SequenceEqual(premul.Red) && bgraPremul.Green.SequenceEqual(premul.Green),
    "premultiplied BGRA and RGBA agree");
void Reject(Action call, string message) { try { call(); Check(false,message); } catch(ArgumentException) { Check(true,message); } }
Reject(() => PortraitImageAnalysis.Analyze([],0,1,4,PortraitPixelOrder.Rgba),"zero width is rejected");
Reject(() => PortraitImageAnalysis.Analyze([],1,-1,4,PortraitPixelOrder.Rgba),"negative height is rejected");
Reject(() => PortraitImageAnalysis.Analyze([],2,2,7,PortraitPixelOrder.Rgba),"short stride is rejected");
Reject(() => PortraitImageAnalysis.Analyze([],1,1,-4,PortraitPixelOrder.Rgba),"negative stride is rejected explicitly");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0],1,1,4,PortraitPixelOrder.Rgba),"truncated pixel is rejected");
Reject(() => PortraitImageAnalysis.Analyze([],int.MaxValue,int.MaxValue,int.MaxValue,PortraitPixelOrder.Rgba),
    "enormous dimensions fail before arithmetic overflow or allocation");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,(PortraitPixelOrder)9),"unknown channel order is rejected");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,PortraitPixelOrder.Rgba,new(BlackThreshold:-1)),
    "threshold below zero is rejected");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,PortraitPixelOrder.Rgba,new(WhiteThreshold:256)),
    "threshold above 255 is rejected");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,PortraitPixelOrder.Rgba,new(BlackThreshold:200,WhiteThreshold:100)),
    "overlapping black and white thresholds are rejected");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,PortraitPixelOrder.Rgba,new(MinimumAlpha:0)),
    "transparent pixels cannot be opted into statistics");
Reject(() => PortraitImageAnalysis.Analyze([0,0,0,255],1,1,4,PortraitPixelOrder.Rgba,new(AlphaMode:(PortraitPixelAlpha)7)),
    "unknown alpha mode is rejected");
Reject(() => PortraitImageAnalysis.Analyze([129,0,0,128],1,1,4,PortraitPixelOrder.Rgba,
    new(AlphaMode:PortraitPixelAlpha.Premultiplied)),"invalid premultiplied channels are rejected");

byte[] Flat(int width,int height,byte value=80)
{
    var pixels=new byte[width*height*4];
    for(var i=0;i<pixels.Length;i+=4) {pixels[i]=pixels[i+1]=pixels[i+2]=value; pixels[i+3]=255;}
    return pixels;
}
void Square(byte[] pixels,int width,int x,int y,byte color)
{for(var yy=y;yy<y+5;yy++)for(var xx=x;xx<x+5;xx++){var p=(yy*width+xx)*4;pixels[p]=pixels[p+1]=pixels[p+2]=color;}}
var uniform=PortraitImageAnalysis.Analyze(Flat(64,64),64,64,256,PortraitPixelOrder.Rgba);
Check(uniform.Hotspots.Count == 0 && uniform.Attention.All(value => value == 0),"uniform image has no center-bias-only hotspot");
var onePixel=PortraitImageAnalysis.Analyze([12,45,90,255],1,1,4,PortraitPixelOrder.Rgba);
Check(onePixel.Hotspots.Count == 0,"single opaque pixel is not mistaken for a salient edge");
var alphaBorder=Flat(40,40);
for(var y=0;y<40;y++)for(var x=0;x<40;x++)if(x<10||x>=30||y<10||y>=30)alphaBorder[(y*40+x)*4+3]=0;
var borderResult=PortraitImageAnalysis.Analyze(alphaBorder,40,40,160,PortraitPixelOrder.Rgba);
Check(borderResult.Hotspots.Count==0,"alpha silhouette alone does not create contrast with imagined black");
var scene=Flat(96,64);
Square(scene,96,8,8,240);Square(scene,96,43,30,240);Square(scene,96,80,48,240);
var subject=PortraitImageAnalysis.Analyze(scene,96,64,384,PortraitPixelOrder.Rgba);
Check(subject.Hotspots.Count==3,"three separated high contrast regions yield at most three readable candidates");
Check(subject.Hotspots.All(point=>point.Score>0&&point.Score<=1),"hotspot scores are normalized heuristic salience, not percentages");
foreach(var point in subject.Hotspots)
    Check(point.X>=0&&point.Y>=0&&point.Width>0&&point.Height>0&&point.X+point.Width<=96&&point.Y+point.Height<=64,
        "hotspot rectangles stay within source pixels");
for(var i=0;i<subject.Hotspots.Count;i++)for(var j=i+1;j<subject.Hotspots.Count;j++)
{
    var a=subject.Hotspots[i];var b=subject.Hotspots[j];
    Check(a.X+a.Width<=b.X||b.X+b.Width<=a.X||a.Y+a.Height<=b.Y||b.Y+b.Height<=a.Y,
        "reported hotspot rectangles do not overlap");
}
Check(subject.Hotspots.Any(p=>p.X<=10&&p.X+p.Width>=10&&p.Y<=10&&p.Y+p.Height>=10),"left object is represented");
Check(subject.Hotspots.Any(p=>p.X<=45&&p.X+p.Width>=45&&p.Y<=32&&p.Y+p.Height>=32),"middle object is represented");
Check(subject.Hotspots.Any(p=>p.X<=82&&p.X+p.Width>=82&&p.Y<=50&&p.Y+p.Height>=50),"right object is represented");
var symmetric=Flat(100,60);Square(symmetric,100,5,28,240);Square(symmetric,100,48,28,240);
var center=PortraitImageAnalysis.Analyze(symmetric,100,60,400,PortraitPixelOrder.Rgba);
Check(center.Hotspots[0].X<55&&center.Hotspots[0].X+center.Hotspots[0].Width>48,
    "otherwise matching regions weakly prefer the center without fabricating image content");
var downsampled=PortraitImageAnalysis.Analyze(Flat(512,256),512,256,2048,PortraitPixelOrder.Rgba);
Check(downsampled.AttentionWidth<=128&&downsampled.AttentionHeight<=128
    && downsampled.Attention.Count==downsampled.AttentionWidth*downsampled.AttentionHeight,
    "attention working surface is bounded for small local analysis");
var rerun=PortraitImageAnalysis.Analyze(scene,96,64,384,PortraitPixelOrder.Rgba);
Check(subject.Attention.SequenceEqual(rerun.Attention)&&subject.Hotspots.SequenceEqual(rerun.Hotspots),
    "same pixels produce deterministic analysis independent of game state");
Check(subject.Attention.All(value=>float.IsFinite(value)&&value>=0&&value<=1),"all attention values stay finite and normalized");
var chroma=Flat(40,40);
for(var i=0;i<chroma.Length;i+=4){chroma[i]=255;chroma[i+1]=0;chroma[i+2]=0;}
for(var y=15;y<20;y++)for(var x=15;x<20;x++){var p=(y*40+x)*4;chroma[p]=0;chroma[p+1]=76;}
var colored=PortraitImageAnalysis.Analyze(chroma,40,40,160,PortraitPixelOrder.Rgba);
Check(colored.Luma[54]==1600&&colored.Hotspots.Count>0,"equal-luma color contrast remains visible to the heuristic");
var bounded=PortraitImageAnalysis.Analyze(Flat(1024,1024,100),1024,1024,4096,PortraitPixelOrder.Rgba);
Check(bounded.VisiblePixels==1_048_576&&bounded.Luma[100]==1_048_576&&bounded.Hotspots.Count==0,
    "maximum accepted small-image surface has exact counts and no false center hotspot");
Reject(()=>PortraitImageAnalysis.Analyze([],1025,1024,4100,PortraitPixelOrder.Rgba),"image above analysis budget is rejected before allocation");
#else
Check(false,"image analysis is missing: transparent-safe RGB/luma histograms and explicit clipping thresholds cannot run");
Check(false,"attention estimation is missing: uniform-scene rejection and bounded non-overlapping peaks cannot run");
#endif

#if HAS_LIGHT_DIRECTION
void Direction(float vertical,float horizontal,Vector3 expected,string message)
{
    Check(PortraitLightDirection.TryDirection(vertical,horizontal,out var actual),message+" accepts angles");
    Near(actual.X,expected.X,message+" X");Near(actual.Y,expected.Y,message+" Y");Near(actual.Z,expected.Z,message+" Z");
}
Direction(0,0,Vector3.UnitZ,"zero-angle native direction");
Direction(0,90,Vector3.UnitX,"positive horizontal turns toward +X");
Direction(90,0,-Vector3.UnitY,"native positive vertical points down");
Direction(-90,0,Vector3.UnitY,"native negative vertical points up");
Direction(0,180,-Vector3.UnitZ,"backward horizontal direction");
Direction(135,0,new Vector3(0,-.70710677f,-.70710677f),"native vertical above 90 folds without changing direction");
Check(PortraitLightDirection.TryNormalize(135,0,out var folded),"native sphere normalizes both angle fields");
Near(folded.VerticalDegrees,45,"folded vertical uses canonical hemisphere");Near(folded.HorizontalDegrees,-180,"folded vertical flips longitude");
Check(PortraitLightDirection.TryNormalize(720,810,out var wrapped),"finite turn multiples normalize");
Near(wrapped.VerticalDegrees,0,"vertical wraps turns");Near(wrapped.HorizontalDegrees,90,"horizontal wraps turns");
Check(!PortraitLightDirection.TryDirection(float.NaN,0,out _)&&!PortraitLightDirection.TryDirection(0,float.PositiveInfinity,out _),
    "nonfinite angle input is rejected");
Check(!PortraitLightDirection.TryAngles(Vector3.Zero,out _)&&!PortraitLightDirection.TryAngles(new(float.NaN,0,1),out _),
    "zero and nonfinite direction vectors are rejected");
Check(PortraitLightDirection.TryAngles(new(float.MaxValue,float.MaxValue,0),out var huge),"large finite vector avoids overflow on normalization");
Near(huge.VerticalDegrees,-45,"large vector latitude");Near(huge.HorizontalDegrees,90,"large vector longitude");
Check(PortraitLightDirection.TryAngles(new(float.Epsilon,0,0),out var tiny),"subnormal finite vector retains its direction");
Near(tiny.HorizontalDegrees,90,"subnormal direction longitude");
Check(PortraitLightDirection.TryNative(-180,180,out var nativeExtreme),"native signed-short endpoints are accepted");
Check(!PortraitLightDirection.TryNative(-181,0,out _)&&!PortraitLightDirection.TryNative(0,181,out _),"native out-of-range angle is rejected");
Check(PortraitLightDirection.TryQuantize(44.6f,-179.6f,out var verticalInt,out var horizontalInt),"drag values can be quantized to native integer degrees");
Check(verticalInt==45&&horizontalInt==-180,"quantized angles round then normalize deterministically");
Check(!PortraitLightDirection.TryQuantize(float.NegativeInfinity,0,out _,out _),"quantization rejects infinity");

Check(PortraitLightDirection.TryProject(0,0,PortraitLightView.Top,out var top),"top view projects valid direction");
Near(top.X,0,"top view front X");Near(top.Y,-1,"top view front maps screen up");
Check(PortraitLightDirection.TryProject(-90,0,PortraitLightView.Side,out var side),"side view projects valid direction");
Near(side.X,0,"side view zenith X");Near(side.Y,-1,"side view zenith maps screen up");
Check(!PortraitLightDirection.TryProject(0,0,(PortraitLightView)9,out _),"unknown projection view is rejected");
Check(PortraitLightDirection.TryFromProjection(new(2,0),PortraitLightView.Top,false,out var rim),"drag beyond circle clamps to rim");
Near(rim.HorizontalDegrees,90,"outside drag direction is clamped without flipping axis");
Check(!PortraitLightDirection.TryFromProjection(new(float.NaN,0),PortraitLightView.Side,false,out _),"nonfinite screen point is rejected");
Check(PortraitLightDirection.TryFromProjection(new(0,0),PortraitLightView.Top,false,out var above),"top view hidden positive hemisphere can be chosen");
Near(above.VerticalDegrees,-90,"top center positive hidden component points up");
Check(PortraitLightDirection.TryFromProjection(new(0,0),PortraitLightView.Top,true,out var below),"top view hidden negative hemisphere can be chosen");
Near(below.VerticalDegrees,90,"top center negative hidden component points down");
foreach(var v in new[]{-180,-135,-90,-45,0,45,90,135,180})foreach(var h in new[]{-180,-120,-60,0,60,120,180})
{
    PortraitLightDirection.TryDirection(v,h,out var vector);
    Check(PortraitLightDirection.TryAngles(vector,out var angles),"angle inversion accepts native sphere direction");
    PortraitLightDirection.TryDirection(angles.VerticalDegrees,angles.HorizontalDegrees,out var inverse);
    Check(Vector3.Distance(vector,inverse)<.00001f,"sphere angles round-trip as direction even at poles and wrap boundaries");
    foreach(var view in new[]{PortraitLightView.Top,PortraitLightView.Side})
    {
        PortraitLightDirection.TryProject(v,h,view,out var point);
        var negative = view==PortraitLightView.Top ? vector.Y<0 : vector.X<0;
        Check(PortraitLightDirection.TryFromProjection(point,view,negative,out var restored),"projected point inverts with explicit hidden hemisphere");
        PortraitLightDirection.TryDirection(restored.VerticalDegrees,restored.HorizontalDegrees,out var lifted);
        Check(Vector3.Distance(vector,lifted)<.0005f,"top/side projection preserves direction with hidden-axis choice");
    }
}
#else
Check(false,"light direction math is missing: normalized native-angle sphere and reversible top/side projections cannot run");
#endif

#if HAS_ANALYSIS_REQUEST && HAS_IMAGE_ANALYSIS
TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task WaitUntil(Func<bool> ready)
{
    var deadline=DateTime.UtcNow.AddSeconds(5);
    while(!ready()) {if(DateTime.UtcNow>=deadline) throw new TimeoutException("Analysis lifecycle failed to settle."); await Task.Delay(1);}
}
var sample=PortraitImageAnalysis.Analyze([100,100,100,255],1,1,4,PortraitPixelOrder.Rgba);
using(var request=new PortraitAnalysisRequest())
{
    var entered=Signal<bool>();var pending=Signal<PortraitImageAnalysisResult>();var duplicateCalls=0;
    Check(request.TryStart("character-a|state-1",token=>{entered.SetResult(true);return pending.Task;}),"first analysis request starts");
    Check(request.IsBusy&&request.CurrentResult is null&&request.CurrentKey=="character-a|state-1","pending request exposes its identity without an old result");
    Check(!request.TryStart("character-a|state-2",_=>{Interlocked.Increment(ref duplicateCalls);return Task.FromResult(sample);}),
        "only one analysis task is in flight");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(duplicateCalls==0,"rejected concurrent request does not execute another worker");
    pending.SetResult(sample);await WaitUntil(()=>!request.IsBusy);
    Check(ReferenceEquals(request.CurrentResult,sample)&&request.CurrentKey=="character-a|state-1"&&request.Error is null,
        "completed result is published only under its request identity");
    Check(!request.TryStart("character-a|state-1",_=>Task.FromResult(sample)),"completed identical state uses cached result without another readback");
    request.Invalidate();
    Check(request.CurrentResult is null&&request.CurrentKey is null&&request.Error is null,"closing the analysis view clears published state immediately");
}
using(var request=new PortraitAnalysisRequest())
{
    var entered=Signal<CancellationToken>();var pending=Signal<PortraitImageAnalysisResult>();
    Check(request.TryStart("character-a|old-camera",token=>{entered.SetResult(token);return pending.Task;}),"stale-result test starts old state");
    var token=await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    request.Invalidate();
    Check(request.CurrentKey is null&&request.CurrentResult is null,"character or camera invalidation removes old identity before completion");
    Check(request.IsBusy&&!request.TryStart("character-b|new-camera",_=>Task.FromResult(sample)),
        "cancellation-ignoring old task still occupies the single in-flight slot");
    await WaitUntil(()=>token.IsCancellationRequested);
    pending.SetResult(sample);await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentResult is null&&request.CurrentKey is null&&request.Error is null,"late old-character result cannot publish after invalidation");
    Check(request.TryStart("character-b|new-camera",_=>Task.FromResult(sample)),"new character can start when old task has released its slot");
    await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentKey=="character-b|new-camera"&&ReferenceEquals(request.CurrentResult,sample),"only the new character receives the new result");
}
using(var request=new PortraitAnalysisRequest())
{
    Check(request.TryStart("retry",_=>throw new InvalidOperationException("test-only private implementation detail")),"synchronously throwing worker is accepted and observed asynchronously");
    await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentResult is null&&!string.IsNullOrWhiteSpace(request.Error),"failed analysis exposes a retryable failure");
    Check(!request.Error!.Contains("private implementation detail"),"player-facing failure does not expose raw worker implementation details");
    Check(request.TryStart("retry",_=>Task.FromResult(sample)),"failed state can be retried without a state change");
    await WaitUntil(()=>!request.IsBusy);
    Check(request.Error is null&&ReferenceEquals(request.CurrentResult,sample),"successful retry clears previous failure");
}
var disposedRequest=new PortraitAnalysisRequest();
{
    var entered=Signal<bool>();var pending=Signal<PortraitImageAnalysisResult>();
    Check(disposedRequest.TryStart("late",_=>{entered.SetResult(true);return pending.Task;}),"dispose test starts real pending task");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    disposedRequest.Dispose();
    Check(!pending.Task.IsCompleted&&disposedRequest.CurrentResult is null,"dispose returns without waiting for pending worker");
    Check(!disposedRequest.TryStart("after-dispose",_=>Task.FromResult(sample)),"disposed service cannot acquire a new request");
    pending.SetResult(sample);await WaitUntil(()=>!disposedRequest.IsBusy);
    Check(disposedRequest.CurrentResult is null&&disposedRequest.CurrentKey is null&&disposedRequest.Error is null,
        "late completion after dispose releases slot without publishing");
    disposedRequest.Dispose();
}
using(var request=new PortraitAnalysisRequest())
{
    var callbackEntered=Signal<bool>();var callbackRelease=Signal<bool>();var workerEntered=Signal<bool>();var pending=Signal<PortraitImageAnalysisResult>();
    Check(request.TryStart("nonblocking",token=>
    {
        token.Register(()=>{callbackEntered.TrySetResult(true);callbackRelease.Task.GetAwaiter().GetResult();});
        workerEntered.SetResult(true);return pending.Task;
    }),"cancellation callback test starts a real task");
    await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var dispose=Task.Run(request.Dispose);
    var first=await Task.WhenAny(dispose,Task.Delay(TimeSpan.FromSeconds(2)));
    Check(ReferenceEquals(first,dispose),"dispose does not synchronously wait for arbitrary cancellation callbacks");
    await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!callbackRelease.Task.IsCompleted,"registered callback actually runs while its release is still blocked");
    callbackRelease.TrySetResult(true);await dispose.WaitAsync(TimeSpan.FromSeconds(5));
    pending.SetResult(sample);await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentResult is null,"callback cancellation cannot resurrect disposed state");
}
using(var request=new PortraitAnalysisRequest())
{
    try {request.TryStart("   ",_=>Task.FromResult(sample));Check(false,"empty request identity is rejected");}
    catch(ArgumentException) {Check(true,"empty request identity is rejected");}
    try {request.TryStart("null-worker",null!);Check(false,"null worker is rejected");}
    catch(ArgumentException) {Check(true,"null worker is rejected");}
    Check(!request.IsBusy&&request.CurrentKey is null,"invalid request input never acquires a slot");
    Check(request.TryStart("null-result",_=>Task.FromResult<PortraitImageAnalysisResult>(null!)),"null-result worker starts as detached task");
    await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentResult is null&&request.Error is not null,"null result fails rather than publishing a false success");
}
using(var request=new PortraitAnalysisRequest())
{
    var entered=Signal<bool>();var pending=Signal<PortraitImageAnalysisResult>();
    request.TryStart("stale-failure",_=>{entered.SetResult(true);return pending.Task;});
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));request.Invalidate();
    pending.SetException(new InvalidOperationException("late stale failure"));await WaitUntil(()=>!request.IsBusy);
    Check(request.CurrentResult is null&&request.Error is null,"late exception from an invalidated request never reaches the new scene");
}
for(var attempt=0;attempt<10;attempt++)
{
    var request=new PortraitAnalysisRequest();var released=Signal<bool>();var invoked=0;
    Check(request.TryStart("accepted-lease",token=>
    {
        Interlocked.Increment(ref invoked);
        try {token.ThrowIfCancellationRequested();return Task.FromResult(sample);}
        finally {released.TrySetResult(true);}
    }),"accepted factory owns its captured cleanup even if canceled before thread-pool dispatch");
    request.Dispose();await released.Task.WaitAsync(TimeSpan.FromSeconds(5));await WaitUntil(()=>!request.IsBusy);
    Check(invoked==1&&request.CurrentResult is null,"accepted canceled factory runs exactly once and cannot publish after dispose");
}
#else
Check(false,"analysis request lifecycle is missing: concurrent work, stale identities, cancellation and late dispose completion cannot run");
#endif

#if HAS_TONE_TARGET
Check(PortraitToneTarget.TryCreate(64,128,192,out var identityTarget),"identity target accepts native encoded range");
for(var i=0;i<256;i++)Check(identityTarget!.Map(i)==i,"identity target preserves each encoded luma value");
Check(PortraitToneTarget.TryCreate(32,96,192,out var darkTarget),"three fixed control points can express a bounded darker target");
Check(darkTarget!.Map(0)==0&&darkTarget.Map(255)==255,"curve endpoints stay fixed instead of offering arbitrary black/white remapping");
Check(darkTarget.Map(32)==16&&darkTarget.Map(96)==64&&darkTarget.Map(160)==144,
    "target between control points uses piecewise linear display-space interpolation");
Check(!PortraitToneTarget.TryCreate(-1,128,192,out _)&&!PortraitToneTarget.TryCreate(64,128,256,out _),"target output cannot leave 0..255 range");
Check(!PortraitToneTarget.TryCreate(100,99,192,out _)&&!PortraitToneTarget.TryCreate(64,200,199,out _),"descending target segments are rejected");
Check(PortraitToneTarget.TryCreate(0,0,255,out var flatSegments),"nondecreasing target can retain flat regions");
for(var i=1;i<256;i++)Check(flatSegments!.Map(i)>=flatSegments.Map(i-1),"quantized target never reverses tonal order");
try {darkTarget.Map(-1);Check(false,"target rejects negative input instead of wrapping");}
catch(ArgumentException){Check(true,"target rejects negative input instead of wrapping");}
try {darkTarget.Map(256);Check(false,"target rejects out-of-range input instead of wrapping");}
catch(ArgumentException){Check(true,"target rejects out-of-range input instead of wrapping");}
Check(darkTarget.ControlPoints.SequenceEqual(new byte[]{0,32,96,192,255}),"target exposes only immutable output control samples at fixed input positions");
#else
Check(false,"restricted tone target is missing: fixed-point monotonic display-space goal cannot run");
#endif

foreach(var failure in failures) Console.Error.WriteLine("FAIL: "+failure);
Console.WriteLine($"PortraitAnalysisTests: {checks-failures.Count} passed, {failures.Count} failed");
return failures.Count==0?0:1;
