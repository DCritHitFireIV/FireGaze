using FireGaze.PortraitPreview;

internal static class PortraitLightingSuppressionCases
{
    public static void Run(string root, Action<bool,string> check)
    {
        using var feature=new PortraitPreviewFeature(Path.Combine(root,"lighting-suppression.json"));
        feature.Sample();feature.OpenPreview();
        var before=feature.Store.Count;
        feature.SetFitSuppression(true);
        feature.EditComposition(c=>c with {DirectionalRed=31,DirectionalGreen=32});
        feature.Sample();feature.Advance(2000);feature.Sample();
        check(feature.Store.Count==before,"probe colors never enter ordinary native-history sampling");
        feature.RecordDirect("颜色探针");
        check(feature.Store.Count==before,"direct-history observer cannot publish a suppressed probe");
        feature.SetFitSuppression(false);
        feature.Sample();feature.Advance(2000);feature.Sample();
        check(feature.Store.Count==before+1,"ordinary history observation resumes after the fit transaction");

        TemplateReadsRestoredRgb(root,check);
        CleanupRefusalBlocksEditing(root,check);
        NativeRefusalKeepsCompensatedState(root,check);
    }

    private static void TemplateReadsRestoredRgb(string root,Action<bool,string> check)
    {
        using var feature=TemplateFeature(root,"template-fit-cancel");
        var count=feature.Store.Count;
        Probe(feature);
        feature.FitCancellation=()=>
        {
            RestoreRgb(feature);
            feature.SetFitSuppression(false);
            return true;
        };
        feature.ApplyGlamourPlate(0);
        check(HasRestoredRgb(feature.Native.Snapshot.Composition),
            "template captures compensated six-channel RGB before building its full editor replacement");
        check(feature.Native.Snapshot.Look.Pieces[3]==new PortraitPiece(100,1,1)&&feature.Store.Count==count+1,
            "template after fit cancellation applies selected equipment as one history transaction");
        var record=PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value).First().SnapshotJSON)!;
        check(HasRestoredRgb(record.Composition),
            "template history persists compensated RGB instead of the canceled probe colors");
    }

    private static void CleanupRefusalBlocksEditing(string root,Action<bool,string> check)
    {
        foreach(var operation in new[]{"template","save-favorite","restore-favorite","appearance","undo","redo","restore-history","mirror","owned-equipment"})
        {
            using var feature=TemplateFeature(root,"fit-pending-"+operation);
            feature.SaveFavorite("灯光前收藏",[]);
            var favorite=feature.Favorites.Single().ID;
            var record=feature.History.First().ID;
            var source=new OwnedSourceKey(OwnedSourceKind.Inventory);
            feature.SetOwnedItems(new(feature.Owner,[new(100,[new(source,100,1,0,1,1,1,0,false,OwnedReadStatus.Current)])],[]),
                PortraitSlot.Body,[new(100,"装备",1,2,true,"")]);
            Probe(feature);
            feature.FitCancellation=()=>false;
            var before=feature.Native.Snapshot.ToJSON();
            var captures=feature.Native.EditorCaptureCalls;
            var applies=feature.Native.ApplyCalls;
            var count=feature.Store.Count;
            var cursor=feature.Store.CurrentRecordID(feature.Key!.Value);
            var favorites=feature.Favorites.Count;
            switch(operation)
            {
                case "template":feature.ApplyGlamourPlate(0);break;
                case "save-favorite":feature.SaveFavorite("禁止记录探针",[]);break;
                case "restore-favorite":feature.RestoreFavorite(favorite);break;
                case "appearance":feature.ChangeAppearance("hair",2);break;
                case "undo":feature.Undo();break;
                case "redo":feature.Redo();break;
                case "restore-history":feature.RestoreHistory(record);break;
                case "mirror":feature.SetSyncPlate(true);break;
                case "owned-equipment":feature.EquipFromSources(PortraitSlot.Body,100,[]);break;
            }
            check(feature.Native.Snapshot.ToJSON()==before&&feature.Native.ApplyCalls==applies
                &&feature.Store.Count==count&&feature.Store.CurrentRecordID(feature.Key.Value)==cursor
                &&feature.Favorites.Count==favorites&&!feature.SyncPlate,
                operation+" cannot edit or publish probe history while lighting cleanup remains pending");
            check(feature.Native.EditorCaptureCalls==captures,
                operation+" does not capture an editor snapshot before lighting cleanup succeeds");
        }
    }

    private static void NativeRefusalKeepsCompensatedState(string root,Action<bool,string> check)
    {
        using var feature=TemplateFeature(root,"template-fit-native-refusal");
        var before=feature.Native.Snapshot.ToJSON();
        var count=feature.Store.Count;
        var cursor=feature.Store.CurrentRecordID(feature.Key!.Value);
        Probe(feature);
        feature.FitCancellation=()=>{RestoreRgb(feature);feature.SetFitSuppression(false);return true;};
        feature.Native.RejectApply=true;
        feature.ApplyGlamourPlate(0);
        check(feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count
            &&feature.Store.CurrentRecordID(feature.Key.Value)==cursor,
            "native template refusal preserves the compensated editor and history cursor without recording the probe");
    }

    private static PortraitPreviewFeature TemplateFeature(string root,string name)
    {
        var directory=Path.Combine(root,name);
        Directory.CreateDirectory(directory);
        var feature=new PortraitPreviewFeature(Path.Combine(directory,"history.json"));
        feature.SetLibraryValidation([new(100,1<<3,1,2)]);
        RestoreRgb(feature);
        feature.Sample();feature.OpenPreview();
        var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();
        pages[0].Pieces[3]=new(100,1,1);
        feature.SetPlateObservations(pages);
        return feature;
    }

    private static void Probe(PortraitPreviewFeature feature)
    {
        feature.SetFitSuppression(true);
        feature.EditComposition(c=>c with
        {
            DirectionalRed=201,DirectionalGreen=202,DirectionalBlue=203,
            AmbientRed=204,AmbientGreen=205,AmbientBlue=206,
        });
    }

    private static void RestoreRgb(PortraitPreviewFeature feature) => feature.EditComposition(c=>c with
    {
        DirectionalRed=11,DirectionalGreen=22,DirectionalBlue=33,
        AmbientRed=44,AmbientGreen=55,AmbientBlue=66,
    });

    private static bool HasRestoredRgb(PortraitComposition composition) => composition is
    {
        DirectionalRed:11,DirectionalGreen:22,DirectionalBlue:33,
        AmbientRed:44,AmbientGreen:55,AmbientBlue:66,
    };
}
