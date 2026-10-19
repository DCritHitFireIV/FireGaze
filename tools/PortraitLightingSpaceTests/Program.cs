using System.Numerics;
using FireGaze.PortraitPreview;

var passed=0; var failed=0;
void Check(bool ok,string label){Console.WriteLine((ok?"PASS ":"FAIL ")+label);if(ok)passed++;else failed++;}

var composition=new PortraitComposition
{
    CameraPosition=new PortraitVector4(0,0,1.2f,0),
    CameraTarget=new PortraitVector4(0,0,0,0),
    CameraYaw=0, CameraPitch=0, CameraDistance=1.2f,
    CameraZoomNormalized=.48f, DirectionalVerticalAngle=20, DirectionalHorizontalAngle=-35,
    DirectionalRed=255, DirectionalGreen=255, DirectionalBlue=255,
    AmbientRed=48, AmbientGreen=48, AmbientBlue=48, AmbientBrightness=127
};
var space=PortraitLightingSpace.From(composition);
Check(Math.Abs(space.Camera.Distance-1.2f)<.001f,"space starts from native camera distance");
space.Orbit(new Vector2(200,-200),200);
Check(space.Camera.ElevationRadians < 1.25f && space.Camera.ElevationRadians > -1.25f,"camera orbit clamps elevation to a safe native range");
space.Dolly(-100);
Check(space.Camera.Distance <= PortraitLightingSpace.MaximumDistance && space.Camera.Distance >= PortraitLightingSpace.MinimumDistance,"camera dolly stays inside native range");
Check(space.TrySetLightFromViewport(new Vector2(.75f,.5f)),"light marker accepts finite viewport coordinates");
var applied=space.ApplyTo(composition);
Check(float.IsFinite(applied.CameraPosition.X) && float.IsFinite(applied.CameraPosition.Y) && float.IsFinite(applied.CameraPosition.Z),"space camera produces finite native position");
Check(Math.Abs(applied.CameraPosition.Z-applied.CameraDistance)>.001f || Math.Abs(applied.CameraPosition.X)>.001f,"orbit actually moves native camera position");
Check(applied.DirectionalVerticalAngle != composition.DirectionalVerticalAngle || applied.DirectionalHorizontalAngle != composition.DirectionalHorizontalAngle,"light interaction produces native directional angles");
var projection=space.Project(new Vector3(0,0,0));
Check(projection.IsVisible && float.IsFinite(projection.Screen.X) && float.IsFinite(projection.Screen.Y),"space projects subject into the viewport");

var offsetComposition=composition with { CameraTarget=new PortraitVector4(.42f,.18f,-.27f,0) };
var offsetSpace=PortraitLightingSpace.From(offsetComposition);
var offsetSubject=offsetSpace.Project(Vector3.Zero);
Check(offsetSubject.IsVisible && Vector2.Distance(offsetSubject.Screen,new(160,137.8f))<1,"nonzero native camera target keeps local subject centered");

var wheel=new PortraitSpaceWheelBatch();
wheel.Observe(1);
Check(wheel.TryCommit(0,false),"quiet wheel batch commits once after the wheel stops");
Check(!wheel.TryCommit(0,false),"quiet wheel batch cannot commit twice");
wheel.Observe(-1);
Check(!wheel.TryCommit(0,true),"wheel batch waits while another gesture is active");
Check(wheel.TryCommit(0,false),"wheel batch commits after the competing gesture ends");

var liveInfo = new PortraitSpaceLiveImageInfo(PortraitImageScope.Portrait, 800, 600);
Check(PortraitLightingSpaceImage.TryFit(liveInfo, PortraitImageScope.Portrait, new(400, 300), out var liveFit),
    "live portrait image fits into the reference viewport");
Check(liveFit.Size == new Vector2(400, 300), "live portrait image preserves aspect ratio");
Check(!PortraitLightingSpaceImage.TryFit(liveInfo with { Scope = PortraitImageScope.VisibleAdventurerPlate },
    PortraitImageScope.Portrait, new(400, 300), out _), "plate capture is not shown as a portrait image");
Check(!PortraitLightingSpaceImage.TryFit(liveInfo with { Width = 0 }, PortraitImageScope.Portrait,
    new(400, 300), out _), "zero-sized live image stays in waiting state");
Check(!PortraitLightingSpaceImage.TryFit(null, PortraitImageScope.Portrait, new(400, 300), out _),
    "missing live image stays in waiting state");
#if HAS_OBSERVER
var observer = new PortraitSpaceObserver();
var viewportSize = new Vector2(500, 360);
var overview = observer.Project(Vector3.Zero, viewportSize);
Check(overview.IsVisible && Vector2.Distance(overview.Screen, new(250, 180)) < .01f,
    "independent observer keeps the subject at the viewport center");
var nativeCamera = PortraitLightingSpace.From(composition);
var cameraMark = observer.Project(nativeCamera.CameraOffset(), viewportSize);
Check(cameraMark.IsVisible && Vector2.Distance(cameraMark.Screen, overview.Screen) > 20,
    "native portrait camera is visible from an independent observation point");
var nativeBefore = nativeCamera.ApplyTo(composition);
observer.Orbit(new(80, -30), 360);
Check(nativeCamera.ApplyTo(composition) == nativeBefore,
    "rotating the observation view never changes the portrait camera");
Check(Vector2.Distance(observer.Project(nativeCamera.CameraOffset(), viewportSize).Screen, cameraMark.Screen) > 5,
    "observation orbit changes the camera marker projection");
Check(observer.TrySphereDirection(observer.Project(new Vector3(0, 0, 1.55f), viewportSize).Screen,
        viewportSize, 1.55f, new(0, 0, 1), out var picked) && Vector3.Distance(picked, new(0, 0, 1)) < .001f,
    "light picking preserves the nearest existing hemisphere instead of jumping through the subject");
Check(!observer.TrySphereDirection(new(float.NaN, 0), viewportSize, 1.55f, Vector3.UnitZ, out _),
    "light picking rejects nonfinite pointer coordinates");
Check(!observer.Project(Vector3.Zero, new(0, 0)).IsVisible,
    "reference projection rejects a collapsed viewport");
#else
Check(false, "independent observation camera and light picking are not implemented");
#endif
Console.WriteLine($"PortraitLightingSpaceTests: {passed} passed, {failed} failed");
return failed==0?0:1;
