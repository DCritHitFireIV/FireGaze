using FireGaze.PortraitPreview;
var checks=0;
void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS "+name);}
#if HAS_PALETTE
var input=new[] {new PortraitDye(8,"红后",0,2,2),new PortraitDye(91,"灰前",0,1,1),new PortraitDye(7,"红前",0,2,1),new PortraitDye(0,"无",0,0,0),new PortraitDye(56,"灰后",0,1,2)};
var groups=PortraitDyePalette.Group(input);
Check(groups.Count==2,"native shade categories exclude the clear sentinel");
Check(groups[0].Shade==1&&groups[1].Shade==2,"native shade order wins over item IDs and input order");
Check(groups[0].Dyes.Select(x=>x.ID).SequenceEqual(new byte[]{91,56}),"dyes within a shade use native SubOrder");
Check(groups[1].Dyes.Select(x=>x.ID).SequenceEqual(new byte[]{7,8}),"every shade keeps its native suborder");
Check(groups.SelectMany(x=>x.Dyes).Count()==4,"grouping keeps every valid dye exactly once");
Check(groups[0].Name.Contains("黑")&&groups[1].Name.Contains("红"),"categories use recognizable color names");
var extra=PortraitDyePalette.Group([new(9,"未来类别",0,99,1)]);
Check(extra.Count==1&&extra[0].Name.Contains("99"),"unknown native shades remain visible with a category label");
Check(PortraitDyePalette.Group([]).Count==0,"an empty catalog is a genuine empty palette");
#else
Check(false,"portrait dye palette category implementation is required");
#endif
var fake=new SessionTarget();
var session=new PortraitPreviewSession(fake);
Check(session.Open(fake.Identity),"external history test opens a real preview lease");
var observe=typeof(PortraitPreviewSession).GetMethod("ObserveLook",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
Check(observe is not null,"a successfully restored external snapshot can update the session view");
var changed=fake.Look.Copy();changed.Pieces[2]=new(77,5,6);
Check((bool)observe!.Invoke(session,[changed])!,"a current lease accepts an already applied external look");
Check(session.Current!.Pieces[2].ItemID==77&&fake.Applies==0,"observing external changes updates controls without applying twice");
changed.Pieces[2]=new(99,0,0);
Check(session.Current.Pieces[2].ItemID==77,"the session detaches the externally supplied mutable array");
Check(session.Close()&&fake.Look.Pieces[2].ItemID==1,"closing after historical restore retains the original wardrobe baseline");
Check(!(bool)observe.Invoke(session,[changed])!,"an inactive session cannot acquire a snapshot by observation");
Console.WriteLine($"PortraitEditingTests: {checks} passed, 0 failed");

sealed class SessionTarget:IPortraitPreviewTarget
{
    public PortraitIdentity Identity=new(1,2,3,4,5);
    public PortraitLook Look=new(Enumerable.Repeat(new PortraitPiece(1,0,0),13).ToArray(),true,true,false);
    public int Applies;
    public bool IsCurrent(PortraitIdentity identity)=>identity==Identity;
    public PortraitLook? Capture(PortraitIdentity identity)=>IsCurrent(identity)?Look.Copy():null;
    public bool Apply(PortraitIdentity identity,PortraitLook look){if(!IsCurrent(identity))return false;Applies++;Look=look.Copy();return true;}
}
