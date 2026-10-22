using FireGaze.PortraitPreview;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using System.Text.Json;

var checks = 0;
void Check(bool valid, string name) { checks++; if (!valid) throw new Exception(name); }
void Throws(System.Action action, string name) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(name); }
var raw = Enumerable.Range(0, 26).Select(i => (byte)(170 + i)).ToArray();
raw[0] = 1; raw[1] = 0; raw[4] = 1; raw[5] = 2;
var values = new[] { new PortraitAppearanceOption(3, "发型 3", 100), new PortraitAppearanceOption(7, "发型 7", 101), new PortraitAppearanceOption(9, "无效发型", Available: false, Reason: "不适用于当前脸型") };
var fields = new[]
{
    new PortraitAppearanceField("hair", "发型", 6, 255, 0, values),
    new PortraitAppearanceField("highlight", "挑染", 7, 128, 7, [new(0,"关闭"),new(1,"开启")]),
    new PortraitAppearanceField("feature5", "伤痕", 12, 16, 4, [new(0,"关闭"),new(1,"开启")]),
    new PortraitAppearanceField("lipstick", "唇彩", 19, 128, 7, [new(0,"关闭"),new(1,"开启")]),
    new PortraitAppearanceField("paint", "面妆", 24, 127, 0, [new(0,"无"),new(3,"面妆 3")]),
    new PortraitAppearanceField("paintReverse", "反转面妆", 24, 128, 7, [new(0,"关闭"),new(1,"开启")]),
    new PortraitAppearanceField("paintColor", "面妆颜色", 25, 255, 0, [new(95,"深色",Color:0xff123456),new(128,"浅色",Color:0xff654321)])
};
var profile = new PortraitAppearanceProfile(new(1,1,0,2), fields);
var catalog = new PortraitAppearanceCatalog([profile]);
var groups = catalog.Build(raw);
Check(groups.Count == fields.Length, "Current race/tribe/sex/face exposes its actual menu");
Check(groups.Single(x=>x.ID=="hair").CurrentValue == raw[6], "Current value is read without inventing fallback");
Check(groups.Single(x=>x.ID=="highlight").CurrentValue == ((raw[7]>>7)&1), "Bit control decode");
foreach(var field in fields)
foreach(var option in field.Options.Where(x=>x.Available))
{
    Check(catalog.TryChange(raw,field.ID,option.Value,out var changed,out var reason), $"Valid option {field.ID}/{option.Value}: {reason}");
    Check(changed.Length==26 && !ReferenceEquals(changed,raw), "A new full snapshot is returned");
    Check((changed[field.ByteIndex]&field.Mask)==((option.Value<<field.Shift)&field.Mask), "Encoded field value");
    for(int i=0;i<26;i++)
        Check(i==field.ByteIndex ? (changed[i]&~field.Mask)==(raw[i]&~field.Mask) : changed[i]==raw[i], $"No unrelated field/bit changed {field.ID} byte {i}");
}
Check(!catalog.TryChange(raw,"hair",9,out var unavailable,out var unavailableReason) && unavailable.Length==0 && unavailableReason.Contains("脸型"), "Unavailable option rejected with reason");
Check(!catalog.TryChange(raw,"hair",999,out _,out _), "Unknown option rejected");
Check(!catalog.TryChange(raw,"height",1,out _,out _), "Non-aesthetic field cannot be requested");
Check(!catalog.TryChange(raw,"hair",-1,out _,out _), "Negative value rejected");
Check(catalog.IsValidChange(raw,raw,out _),"Unchanged original appearance is valid");
foreach(var field in fields)
foreach(var option in field.Options.Where(x=>x.Available))
{
    catalog.TryChange(raw,field.ID,option.Value,out var candidate,out _);
    Check(catalog.IsValidChange(raw,candidate,out _),"Native guard accepts every approved change");
}
foreach(int position in Enumerable.Range(0,26).Except(new[]{6,7,12,19,24,25}))
{
    var unsafeChange=(byte[])raw.Clone(); unsafeChange[position]^=1;
    Check(!catalog.IsValidChange(raw,unsafeChange,out _),$"Native guard rejects non-menu byte {position}");
}
foreach(var pair in new[]{(7,1),(12,128),(19,1)})
{
    var unsafeChange=(byte[])raw.Clone(); unsafeChange[pair.Item1]^=(byte)pair.Item2;
    Check(!catalog.IsValidChange(raw,unsafeChange,out _),$"Native guard rejects protected bit {pair}");
}
var invalidChoice=(byte[])raw.Clone(); invalidChoice[6]=9;
Check(!catalog.IsValidChange(raw,invalidChoice,out _),"Native guard rejects unavailable hair value");
invalidChoice[6]=77;
Check(!catalog.IsValidChange(raw,invalidChoice,out _),"Native guard rejects unknown hair value");
Check(!catalog.IsValidChange(raw,new byte[25],out _),"Native guard rejects truncated target");
Check(!catalog.IsValidChange(new byte[25],raw,out _),"Native guard rejects truncated source");
for(int length=0;length<26;length++)
{
    Check(catalog.Build(raw.AsSpan(0,length)).Count==0,"Truncated snapshot has no choices");
    Check(!catalog.TryChange(raw.AsSpan(0,length),"hair",3,out _,out _),"Truncated snapshot cannot mutate");
}
Check(catalog.Build(new byte[27]).Count==0,"Oversize snapshot rejected");
foreach(int position in new[]{0,1,4,5})
{
    var changed=(byte[])raw.Clone(); changed[position]++;
    Check(catalog.Build(changed).Count==0,$"Foreign profile {position} has no menu");
    Check(!catalog.TryChange(changed,"hair",3,out _,out _),$"Foreign profile {position} rejected");
}
var originalIcon=groups[0].Options[0].Icon;
values[0]=new(11,"外部修改",999);
Check(catalog.Build(raw)[0].Options[0].Icon==originalIcon,"Construction freezes caller-owned options");
fields[0]=fields[0] with { Label="外部修改" };
Check(catalog.Build(raw)[0].Label=="发型","Construction freezes caller-owned fields");
Throws(()=>new PortraitAppearanceCatalog([profile,profile]),"Duplicate profile rejected");
Throws(()=>new PortraitAppearanceCatalog([new(profile.Key,[new("height","身高",3,255,0,[new(1,"1")])])]),"Unsafe byte index rejected");
Throws(()=>new PortraitAppearanceCatalog([new(profile.Key,[new("legacy","旧版刺青",12,128,7,[new(1,"1")])])]),"Legacy tattoo bit remains protected");
Throws(()=>new PortraitAppearanceCatalog([new(profile.Key,[new("hair","发型",6,255,0,[new(300,"300")])])]),"Oversize encoded value rejected");
Throws(()=>new PortraitAppearanceCatalog([new(profile.Key,[new("hair","发型",6,255,0,[new(1,"1"),new(1,"重复")])])]),"Duplicate option values rejected");
Throws(()=>new PortraitAppearanceCatalog([new(profile.Key,[new("feature","特征",12,3,0,[new(0,"0")]),new("overlap","重叠",12,2,1,[new(0,"0")])])]),"Overlapping controls rejected");
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
    var actual=new PortraitAppearanceCatalog(data);
    var snapshot=new byte[26]; snapshot[0]=1; snapshot[4]=1; snapshot[5]=1;
    var actualGroups=actual.Build(snapshot);
    Check(actualGroups.Count>=17,"Real sqpack supplies complete beautician menu");
    Check(actualGroups.Single(g=>g.ID=="hair").Options.Count==102,"All hair options including over 100 entries");
    Check(actualGroups.Single(g=>g.ID=="eyebrows").Options.Select(o=>o.Value).SequenceEqual(Enumerable.Range(0,6)),"Eyebrows native values start at zero");
    Check(actualGroups.Single(g=>g.ID=="hairColor").Options.Count==192,"Native hair color count");
    Check(actualGroups.Single(g=>g.ID=="lipColor").Options.Select(o=>o.Value).SequenceEqual(Enumerable.Range(0,96).Concat(Enumerable.Range(128,96))),"Native dark/light color indices preserve 96..127 gap");
    Check(actualGroups.Single(g=>g.ID=="facePaint").Options.First().Value==0,"Facepaint includes actual no-paint option");
    var namedUnlocks = actualGroups.Single(g=>g.ID=="hair").Options.Where(option=>option.UnlockItemID!=0).ToArray();
    Check(namedUnlocks.Length>0,"Real CN hairstyle options retain structured unlock items");
    var items = data.GetExcelSheet<Item>()!;
    foreach (var option in namedUnlocks)
    {
        var unlock = items.GetRow(option.UnlockItemID);
        Check(!string.IsNullOrWhiteSpace(option.UnlockItemName) && option.UnlockItemName == unlock.Name.ToString()
            && option.UnlockItemIcon == unlock.Icon,"Real CN hairstyle unlock names and icons match their actual item rows");
    }
    var featureCount=0;
    foreach(var row in data.GetExcelSheet<CharaMakeType>()!)
    {
        var faceMenu=row.CharaMakeStruct.First(m=>m.Customize==5);
        for(int f=0;f<faceMenu.SubMenuNum;f++)
        {
            var face=(byte)(row.Race.RowId==7?f+5:f+1);
            snapshot[0]=(byte)row.Race.RowId; snapshot[1]=(byte)row.Gender; snapshot[4]=(byte)row.Tribe.RowId; snapshot[5]=face;
            var built=actual.Build(snapshot);
            Check(built.Count>0,$"Live menu for {row.Race.RowId}/{row.Tribe.RowId}/{row.Gender}/face {face}");
            Check(built.Any(g=>g.ID=="eyeColorLeft") && built.Any(g=>g.ID=="eyeColorRight"),"Both actual iris color options provided");
            foreach(var group in built)
            {
                Check(group.Options.Select(o=>o.Value).Distinct().Count()==group.Options.Count,"Live option values unique");
                foreach(var option in group.Options)
                {
                    var accepted=actual.TryChange(snapshot,group.ID,option.Value,out var next,out var reason);
                    Check(accepted==option.Available,$"Live availability {group.ID}/{option.Value}: {reason}");
                    if(accepted)Check(actual.IsValidChange(snapshot,next,out _),"Applied live option passes native update guard");
                }
                featureCount++;
            }
            if(row.Race.RowId==7)
            {
                Check(built.Any(g=>g.ID=="furPattern") && built.All(g=>g.ID!="lipstick"),"Hrothgar fur patterns do not expose a lipstick toggle");
                var locked=built.Single(g=>g.ID=="hair").Options.Where(o=>!o.Available).ToArray();
                Check(locked.Any(o=>o.Reason?.Contains("脸型")==true),"Hrothgar hair/face restriction is visible");
            }
        }
    }
    Console.WriteLine($"Live game-data menu groups checked: {featureCount}");
}
else Console.WriteLine("SKIP current-client appearance game-data checks: no --sqpack or --game-data argument; base checks only.");
Console.WriteLine($"PASS PortraitAppearanceTests {checks} checks");
