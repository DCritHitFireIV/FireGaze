using FireGaze.PortraitPreview;
var count=0;
void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);count++;Console.WriteLine("PASS "+message);}
using(var feature=new PortraitPreviewFeature()){
 feature.Poll(); Check(feature.Ready,"normal protected preview automatically admits photos while diagnostic capture mode remains off");
 Check(feature.FreshReads==1,"every admitted poll freshly captures the editor rather than trusting a stale observed snapshot");
 feature.ChangeFresh();feature.Poll();Check(!feature.Ready,"fresh state mismatch blocks stale observed history pixels");
}
foreach(var reason in new[]{"closed","save","foreign-session","pointer","keyboard","lightdrag","lighting","census","owned","live","fit","live-physical","fit-physical","native-physical","diagnostic-live","wrong-owner","wrong-generation"}){
 using var feature=new PortraitPreviewFeature();feature.Block(reason);feature.Poll();Check(!feature.Ready,"confirmed "+reason+" prevents capture admission");
}
using(var feature=new PortraitPreviewFeature()){
 feature.SetPhotoPhysical(false);Check(!feature.NativeContext().CapturesDrained,"native live cannot restart while a logical photo slot has unfinished physical work");
 feature.SetPhotoPhysical(true);feature.Poll();Check(!feature.NativeContext().CapturesDrained,"new photo demand suspends native live through ContextBusy before copy admission");
 feature.SetPhotoWanted(false);feature.Poll();Check(feature.NativeContext().CapturesDrained,"completed photo demand automatically restores native live eligibility");
}
Console.WriteLine($"{count}/{count} photo admission checks passed; no game or GPU calls");
