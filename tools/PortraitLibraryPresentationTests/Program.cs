using FireGaze.PortraitPreview;
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); checks++; }
#if !HAS_LIBRARY_PRESENTATION
Check(false, "multi-tag favorites and per-character source guidance are not implemented");
#else
var stamp = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
var entries = new[] {
    new PortraitFavoriteEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),stamp,stamp,1,"角色甲","午后",["暖色","日常"],new()),
    new PortraitFavoriteEntry(Guid.Parse("00000000-0000-0000-0000-000000000002"),stamp,stamp.AddMinutes(1),2,"角色乙","夜色",["冷色"],new()),
    new PortraitFavoriteEntry(Guid.Parse("00000000-0000-0000-0000-000000000003"),stamp,stamp.AddMinutes(2),3,"角色丙","暖夜",["暖色","正式"],new()),
};
var snapshot = entries.Select(e=>e.Name+string.Join(',',e.Tags)).ToArray();
Check(PortraitLibraryPresentation.Filter(entries,"",[]).Select(e=>e.Name).SequenceEqual(["暖夜","夜色","午后"]),"unfiltered favorites are stable latest first");
Check(PortraitLibraryPresentation.Filter(entries,"",["暖色","冷色"]).Count==3,"tags within the selected group are OR");
Check(PortraitLibraryPresentation.Filter(entries,"角色乙",["暖色"]).Count==0,"search and tags intersect without silently clearing selection");
Check(PortraitLibraryPresentation.Filter(entries,"夜",["暖色"]).Single().Name=="暖夜","name search intersects selected tags");
Check(PortraitLibraryPresentation.Filter(entries,"角色甲",[]).Single().Name=="午后","cross character source names are searchable");
Check(entries.Select(e=>e.Name+string.Join(',',e.Tags)).SequenceEqual(snapshot),"browsing never edits library records");
var guide = new PortraitSourceGuideState();
Check(!guide.ShouldShow("",true),"no anonymous character consumes first-open guidance");
Check(guide.ShouldShow("a",true),"first character with unread sources receives guidance");
Check(!guide.ShouldShow("a",true),"same character is not nagged on every open");
Check(guide.ShouldShow("b",true),"another character retains independent first-open guidance");
Check(!guide.ShouldShow("c",false)&&guide.ShouldShow("c",true),"no-data gap appearing later can still receive its first guidance");
var query = new PortraitEquipmentQuery("123",[OwnedSourceKind.GlamourDresser],[0,2],PortraitEquipmentSort.IDDescending);
var eyewear = PortraitLibraryPresentation.ForSlot(query,PortraitSlot.Facewear);
Check(eyewear.Sources?.Count==0&&eyewear.DyeChannels?.Count==0&&eyewear.Search=="123"&&eyewear.Sort==query.Sort,"eyewear browsing ignores inventory and dye restrictions");
Check(query.Sources?.Count==1&&query.DyeChannels?.Count==2,"eyewear never clears the equipment filter selections");
Check(PortraitLibraryPresentation.ForSlot(query,PortraitSlot.Body)==query,"returning to equipment restores its query");
var tagCheckboxMethod = typeof(PortraitLibraryPresentation).GetMethod("TagCheckboxPresentation",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
Check(tagCheckboxMethod is not null,"tag filter exposes a stable checkbox presentation contract");
var warm=PortraitLibraryPresentation.TagCheckboxPresentation("暖色###x");
var cool=PortraitLibraryPresentation.TagCheckboxPresentation("冷色###x");
Check(warm.VisibleText=="暖色###x" && cool.VisibleText=="冷色###x","free-form tag text preserves literal ImGui suffix characters");
Check(warm.ID!=cool.ID && warm.ID.StartsWith("###PortraitFavoriteTag_"),"shared ImGui suffix text cannot merge distinct tag controls");
Check(PortraitLibraryPresentation.TagCheckboxPresentation("冷色###x")==cool,"tag identity remains stable after other tags are reordered");

#endif

#if HAS_FAVORITE_ROW_LAYOUT
// Hand-measured minimum-width scenario: an 80px photo, 8px gap, and a 292px text/action lane.
// Inline reasons formerly pushed edit/delete past this lane. Reason text now occupies its own viewport.
var row = PortraitFavoriteRowLayout.For(380, 1, 17, 21, new(8,4), [108,134,74,40]);
Check(row.TextWidth==292 && row.MetadataHeight==105,"narrow favorite row reserves a separate five-line metadata viewport");
Check(row.Actions.Select(action=>action.Offset).SequenceEqual(new System.Numerics.Vector2[] {new(88,109),new(204,109),new(88,130),new(170,130)}),
    "confirmation actions wrap to a second row without leaving the narrow text lane");
Check(row.Actions.All(action=>action.Offset.X>=88 && action.Offset.X+action.Size.X<=380 && action.Offset.Y>=109),
    "all controls stay horizontally reachable and below the independent reason viewport");
Check(row.RowHeight==151 && row.Actions.All(action=>action.Offset.Y+action.Size.Y<=row.RowHeight-4),
    "uniform clipper row pitch includes every wrapped action and trailing gap");
var wideRow=PortraitFavoriteRowLayout.For(720,1,17,21,new(8,4),[108,134,74,40]);
Check(wideRow.Actions.All(action=>action.Offset.Y==109)&&wideRow.RowHeight==132,
    "wide favorite row fits the same actions on one line while retaining photo height");
foreach(var scale in new[]{.75f,1f,1.5f,2f})
foreach(var width in new[]{380f,420f,720f})
{
    var layout=PortraitFavoriteRowLayout.For(width*scale,scale,17*scale,21*scale,new(8*scale,4*scale),[108*scale,134*scale,74*scale,40*scale]);
    Check(layout.Actions.All(action=>action.Offset.X+action.Size.X<=width*scale+.001f&&action.Offset.Y+action.Size.Y<=layout.RowHeight-4*scale+.001f),
        "favorite action flow remains inside its fixed row across window and font scales");
}
#else
Check(false,"favorite rows require a responsive action layout independent of reason text");
#endif
Console.WriteLine($"PortraitLibraryPresentationTests: {checks} passed, 0 failed");
