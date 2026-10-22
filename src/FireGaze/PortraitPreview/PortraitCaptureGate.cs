namespace FireGaze.PortraitPreview;

internal sealed record PortraitLiveCaptureTicket(long Generation,PortraitCaptureSample Sample);
internal sealed class PortraitCaptureGate(Func<DateTimeOffset> clock)
{
 private PortraitCaptureSample? sample;
 private DateTimeOffset since;
 private long generation;
 private bool attempted;
 public void Observe(PortraitCaptureSample? current)
 {
  if(current==sample)return;
  generation++;sample=current;since=clock();attempted=false;
 }
 public PortraitLiveCaptureTicket? Begin()
 {
  if(sample is null||attempted||clock()-since<TimeSpan.FromMilliseconds(200))return null;
  attempted=true;return new(generation,sample);
 }
 public bool CanAccept(PortraitLiveCaptureTicket ticket)=>sample is not null&&ticket.Generation==generation&&ticket.Sample==sample;
 public void Reset(){generation++;sample=null;attempted=false;}
}
