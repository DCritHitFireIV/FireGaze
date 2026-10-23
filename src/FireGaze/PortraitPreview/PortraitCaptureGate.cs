namespace FireGaze.PortraitPreview;

internal sealed record PortraitLiveCaptureTicket(long Generation,PortraitCaptureSample Sample,PortraitCaptureSurface? Surface);
internal sealed class PortraitCaptureGate(Func<DateTimeOffset> clock)
{
 private PortraitCaptureSample? sample;
 private PortraitCaptureSurface? surface;
 private DateTimeOffset since;
 private long generation;
 private bool attempted;
 public void Observe(PortraitCaptureSample? current,PortraitCaptureSurface? currentSurface)
 {
  if(current==sample&&currentSurface==surface)return;
  generation++;sample=current;surface=currentSurface;since=clock();attempted=false;
 }
 public PortraitLiveCaptureTicket? Begin()
 {
  if(sample is null||attempted||clock()-since<TimeSpan.FromMilliseconds(200))return null;
  attempted=true;return new(generation,sample,surface);
 }
 public bool CanAccept(PortraitLiveCaptureTicket ticket)=>sample is not null&&ticket.Generation==generation&&ticket.Sample==sample&&ticket.Surface==surface;
 public void Reset(){generation++;sample=null;surface=null;attempted=false;}
}
