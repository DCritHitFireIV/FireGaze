using FireGaze.PortraitPreview;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using System.Text.Json;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
var items = Enumerable.Range(0, 12).Select(i => new PortraitValidationItem((uint)i + 100, (ushort)(1 << i), 1, 2)).ToList();
items.Add(new(200, 1 << (int)PortraitSlot.Head, 0, 0));
items.Add(new(201, 1 << (int)PortraitSlot.Head, 1, 0, 1 << 7, 3));
items.Add(new(202, 1 << (int)PortraitSlot.Head, 1, 0, 255, 1 << 1));
items.Add(new(203, 1 << (int)PortraitSlot.Head, 1, 1));
var definitions = new PortraitValidationData(items, [0,1,2], [0,1,2], [0,1,2], [0,1,2], [0,1,2], [0,1,2], [0,1,2], [0,1,2]);
var validator = new PortraitEditorValidation(definitions);
var appearance = new byte[26]; appearance[0] = 1; appearance[4] = 1; appearance[5] = 1;
var original = new PortraitEditorSnapshot { GameBuild = "fixture", EntryPoint = 1, Customize = appearance,
    Look = new(items.Take(12).Select(i => new PortraitPiece(i.ID,1,2)).Append(new(1,0,0)).ToArray(),true,true,false),
    Composition = new() { BannerTimeline = 1, Expression = 1, Background = 1, Frame = 1, Decoration = 1, PoseClassJob = 1, CameraPosition = new(0,1,2,1), CameraTarget = new(0,0,1,1) } };
Check(validator.IsValid(original,out _), "Known native snapshot accepted");
void Reject(PortraitEditorSnapshot candidate, string name) => Check(!validator.IsValid(candidate,out var reason) && !string.IsNullOrWhiteSpace(reason), name);
Reject(original with { EntryPoint = 3 }, "Unknown native editor enum refused");
Reject(original with { EntryPoint = 255 }, "Extreme native editor enum refused");
Reject(original with { Version = 0 }, "Unknown snapshot version refused");
Reject(original with { Customize = new byte[25] }, "Truncated appearance refused");
Reject(original with { Look = new(new PortraitPiece[12],true,true,false) }, "Truncated wardrobe refused");
Check(!validator.IsValid(null,out _), "Null snapshot refused");
foreach(var mutate in new Func<PortraitComposition,PortraitComposition>[] {
    c=>c with {BannerTimeline=ushort.MaxValue},c=>c with {Expression=byte.MaxValue},c=>c with {Background=ushort.MaxValue},
    c=>c with {Frame=ushort.MaxValue},c=>c with {Decoration=ushort.MaxValue},c=>c with {PoseClassJob=short.MaxValue},c=>c with {PoseClassJob=-1}})
    Reject(original with {Composition=mutate(original.Composition)},"Nonexistent sheet token refused");
foreach(var mutate in new Func<PortraitComposition,PortraitComposition>[] {
    c=>c with {CameraPosition=new(65505,0,0,0)},c=>c with {CameraTarget=new(0,-65505,0,0)},
    c=>c with {HeadDirection=new(0,float.MaxValue)},c=>c with {EyeDirection=new(float.MinValue,0)},
    c=>c with {CameraPosition=new(0,0,float.PositiveInfinity,0)},c=>c with {CameraTarget=new(0,0,0,float.NaN)},
    c=>c with {CameraDistance=float.PositiveInfinity},c=>c with {CameraYaw=float.NaN},c=>c with {CameraPitch=float.NegativeInfinity},
    c=>c with {CameraZoomNormalized=float.NaN},c=>c with {AnimationProgress=-1},c=>c with {AnimationProgress=float.NaN}})
    Reject(original with {Composition=mutate(original.Composition)},"Unsafe float/half refused");
var halfLimits=original with {Composition=original.Composition with {CameraPosition=new(65504,-65504,65504,-65504),CameraTarget=new(-65504,65504,-65504,65504),HeadDirection=new(65504,-65504),EyeDirection=new(-65504,65504),CameraZoom=255}};
Check(validator.IsValid(halfLimits,out _),"Half endpoints accepted without guessing camera zoom UI range");
var clear=original.Copy(); Array.Fill(clear.Look.Pieces,default);
Check(validator.IsValid(clear,out _),"All empty equipment and glasses slots are supported");
for(var slot=0;slot<12;slot++)
{
    var wrong=original.Copy(); wrong.Look.Pieces[slot]=new(items[(slot+1)%12].ID,0,0);
    Reject(wrong,$"Wrong native equipment slot {slot} refused");
    var missing=original.Copy(); missing.Look.Pieces[slot]=new(uint.MaxValue,0,0);
    Reject(missing,$"Missing native item row {slot} refused");
    var hq=original.Copy(); hq.Look.Pieces[slot]=new(items[slot].ID+1_000_000,0,0);
    Reject(hq,$"HQ encoded item token {slot} is not silently normalized");
}
foreach(var token in new uint[]{200,201,202})
{
    var invalid=original.Copy(); invalid.Look.Pieces[(int)PortraitSlot.Head]=new(token,0,0);
    Reject(invalid,"Nonrenderable or incompatible metadata refused");
}
var badDye=original.Copy(); badDye.Look.Pieces[0]=badDye.Look.Pieces[0] with {Stain0=255};
Reject(badDye,"Nonexistent stain refused");
var unsupportedDye=original.Copy(); unsupportedDye.Look.Pieces[(int)PortraitSlot.Head]=new(203,1,2);
Reject(unsupportedDye,"Unsupported second dye channel refused");
var noDye=original.Copy(); noDye.Look.Pieces[(int)PortraitSlot.Head]=new(203,2,0);
Check(validator.IsValid(noDye,out _),"Supported first dye channel accepted");
foreach(var glasses in new PortraitPiece[]{new(ushort.MaxValue,0,0),new(uint.MaxValue,0,0),new(1,1,0),new(1,0,1)})
{
    var invalid=original.Copy(); invalid.Look.Pieces[12]=glasses; Reject(invalid,"Invalid facewear token or unsupported stains refused");
}
var unknownRace=original.Copy(); unknownRace.Customize[0]=0; Reject(unknownRace,"Unknown character race refused");
var unknownSex=original.Copy(); unknownSex.Customize[1]=2; Reject(unknownSex,"Unknown character sex refused");
var dirtyClear=clear.Copy(); dirtyClear.Look.Pieces[2]=new(0,1,0); Reject(dirtyClear,"Empty slot cannot carry an ignored stain");
items.Clear();
Check(validator.IsValid(original,out _),"Validation copies caller-owned metadata");
var sqpackIndex = Array.IndexOf(args, "--sqpack");
string? sqpack = null;
if (sqpackIndex >= 0)
{
    if (sqpackIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[sqpackIndex + 1]) || !Directory.Exists(args[sqpackIndex + 1]))
        throw new ArgumentException("--sqpack requires an existing game sqpack directory.");
    sqpack = Path.GetFullPath(args[sqpackIndex + 1]);
}
else if (args.Contains("--game-data"))
{
    var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN","launcherConfigV3.json")));
    sqpack = Path.Combine(config.RootElement.GetProperty("GamePath").GetString()!,"game","sqpack");
}
if (sqpack is not null)
{
    using var data=new GameData(sqpack,new LuminaOptions{DefaultExcelLanguage=Language.ChineseSimplified,PanicOnSheetChecksumMismatch=false});
    var native=new PortraitEditorValidation(data);
    var snapshot=clear with {Composition=new()};
    Check(native.IsValid(snapshot,out _),"Real native zero sentinels are valid");
    foreach(var row in data.GetExcelSheet<BannerTimeline>()!.Where(r=>r.RowId<=ushort.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {BannerTimeline=(ushort)row.RowId}},out _),"Actual timeline token accepted");
    foreach(var row in data.GetExcelSheet<BannerFacial>()!.Where(r=>r.RowId<=byte.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {Expression=(byte)row.RowId}},out _),"Actual facial token accepted");
    foreach(var row in data.GetExcelSheet<BannerBg>()!.Where(r=>r.RowId<=ushort.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {Background=(ushort)row.RowId}},out _),"Actual background token accepted");
    foreach(var row in data.GetExcelSheet<BannerFrame>()!.Where(r=>r.RowId<=ushort.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {Frame=(ushort)row.RowId}},out _),"Actual frame token accepted");
    foreach(var row in data.GetExcelSheet<BannerDecoration>()!.Where(r=>r.RowId<=ushort.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {Decoration=(ushort)row.RowId}},out _),"Actual decoration token accepted");
    foreach(var row in data.GetExcelSheet<ClassJob>()!.Where(r=>r.RowId<=short.MaxValue)) Check(native.IsValid(snapshot with {Composition=snapshot.Composition with {PoseClassJob=(short)row.RowId}},out _),"Actual class-job token accepted");
    foreach(var row in data.GetExcelSheet<Glasses>()!.Where(r=>r.RowId<=ushort.MaxValue && r.Model!=0))
    {
        var candidate=snapshot.Copy(); candidate.Look.Pieces[12]=new(row.RowId,0,0);
        Check(native.IsValid(candidate,out _),"Actual nonzero glasses model accepted");
    }
    foreach(var row in data.GetExcelSheet<Item>()!.Where(r=>r.EquipSlotCategory.RowId!=0 && r.EquipSlotCategory.IsValid))
    {
        var slot=row.EquipSlotCategory.Value;
        var slotValues=new[]{slot.MainHand,slot.OffHand,slot.Head,slot.Body,slot.Gloves,slot.Legs,slot.Feet,slot.Ears,slot.Neck,slot.Wrists,slot.FingerR,slot.FingerL};
        var restriction=row.EquipRestriction.Value;
        var restrictions=new[]{restriction.Hyur,restriction.Elezen,restriction.Lalafell,restriction.Miqote,restriction.Roegadyn,restriction.AuRa,restriction.Hrothgar,restriction.Viera};
        var race=row.EquipRestriction.RowId==0?1:Array.FindIndex(restrictions,x=>x)+1;
        var sex=row.EquipRestriction.RowId==0||restriction.Male?0:restriction.Female?1:-1;
        var firstSlot=Array.FindIndex(slotValues,x=>x>0);
        if(race<1||sex<0||firstSlot<0)continue;
        var candidate=snapshot.Copy();candidate.Customize[0]=(byte)race;candidate.Customize[1]=(byte)sex;
        candidate.Look.Pieces[firstSlot]=new(row.RowId,0,0);
        Check(native.IsValid(candidate,out _)==(row.ModelMain!=0),"Every real equipment row obeys native slot/model/restriction metadata");
    }
    foreach(var row in data.GetExcelSheet<Stain>()!.Where(r=>r.RowId<=byte.MaxValue))
    {
        var wearable=data.GetExcelSheet<Item>()!.First(r=>r.EquipSlotCategory.Value.Head>0 && r.ModelMain!=0 && r.DyeCount>=2 && (r.EquipRestriction.RowId==0 || r.EquipRestriction.Value.Hyur&&r.EquipRestriction.Value.Male));
        var candidate=snapshot.Copy();candidate.Look.Pieces[(int)PortraitSlot.Head]=new(wearable.RowId,(byte)row.RowId,(byte)row.RowId);
        Check(native.IsValid(candidate,out _),"Every real dye token is accepted on a supporting model");
    }
    foreach(var candidate in new[]{snapshot with{Composition=snapshot.Composition with{BannerTimeline=ushort.MaxValue}},
        snapshot with{Composition=snapshot.Composition with{Expression=byte.MaxValue}},snapshot with{Composition=snapshot.Composition with{Background=ushort.MaxValue}},
        snapshot with{Composition=snapshot.Composition with{Frame=ushort.MaxValue}},snapshot with{Composition=snapshot.Composition with{Decoration=ushort.MaxValue}},
        snapshot with{Composition=snapshot.Composition with{PoseClassJob=short.MaxValue}}})
        Check(!native.IsValid(candidate,out _),"Real sheet lookup rejects nonexistent extreme tokens");
}
else Console.WriteLine("SKIP current-client portrait validation game-data checks: no --sqpack or --game-data argument; base checks only.");
Console.WriteLine($"PortraitValidationTests: {checks} passed, 0 failed");
