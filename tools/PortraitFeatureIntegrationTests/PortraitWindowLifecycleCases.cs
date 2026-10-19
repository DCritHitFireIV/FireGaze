using FireGaze.PortraitPreview;

internal static class PortraitWindowLifecycleCases
{
    public static void Run(string root, Action<bool,string> check)
    {
        foreach (var end in new[] { "close", "native-destroyed", "native-replaced", "source-switched", "character-switched" })
        using (var feature=new PortraitPreviewFeature(Path.Combine(root,"windows-"+end+".json")))
        {
            feature.Sample();feature.OpenPreview();feature.OpenHistory();
            var historyBefore=feature.Store.Count;
            switch(end)
            {
                case "close": feature.RollbackAndSync();break;
                case "native-destroyed": feature.Native.Current=false;feature.Sample();break;
                case "native-replaced": feature.Native.Identity=feature.Native.Identity with {Model=77,Generation=78};feature.Sample();break;
                case "source-switched": feature.Native.Snapshot=feature.Native.Snapshot with {EntryPoint=1};feature.Sample();break;
                case "character-switched": feature.Owner=new(100,200);feature.Sample();break;
            }
            check(!feature.HistoryWindowOpen, end+" retires the old editor's auxiliary window intent");
            check(feature.HistoryPhotosView, end+" retains photo/list preference independently of visibility");
            if(end=="close") check(feature.Store.Count==historyBefore,"preview close does not create a history edit merely to retire windows");
        }
        using(var feature=new PortraitPreviewFeature(Path.Combine(root,"windows-hidden-current.json")))
        {
            feature.Sample();feature.OpenPreview();feature.OpenHistory();
            feature.Native.Ready=false;feature.Sample();
            check(feature.HistoryWindowOpen && feature.IsActive,
                "temporary native addon hiding retains current session and auxiliary window intent");
        }
    }
}
