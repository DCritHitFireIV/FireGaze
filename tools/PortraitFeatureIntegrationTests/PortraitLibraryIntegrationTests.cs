using FireGaze.PortraitPreview;

internal static class PortraitLibraryIntegrationTests
{
    public static void Run(string root, Action<bool,string> check)
    {
        string PathFor(string name) {var dir=Path.Combine(root,"library",name);Directory.CreateDirectory(dir);return Path.Combine(dir,"history.json");}
        using(var feature=new PortraitPreviewFeature(PathFor("metadata-drain")))
        {
            feature.RejectFavoriteArchive=true;feature.Sample();feature.OpenPreview();feature.SaveFavorite("参数已保存",[]);feature.WaitFavorites();
            check(feature.Favorites.Count==1 && feature.FavoritesStatus.Contains("照片暂时无法保存"),
                "favorite photo failure leaves durable metadata visible with an explicit photo warning");
            check(feature.FavoriteMetadataDrainSucceeded,
                "feature metadata drain preserves successful metadata independently of an earlier photo archive receipt");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("favorites")))
        {
            feature.Sample();feature.OpenPreview();feature.SaveFavorite("午后",["暖色"]);feature.WaitFavorites();
            check(feature.Favorites.Count==1,"real feature saves adventurer-plate favorite with immediate persistence");
            check(feature.FavoritesStatus.Contains("参数已保存") && !feature.FavoritesStatus.Contains("收藏和照片已保存"),
                "metadata completion cannot claim that a missing favorite photograph is saved");
            feature.FavoritePhotoForTest=PortraitFavoritePhotoStatus.Pending;feature.SampleLibrary();
            check(feature.FavoritesStatus.Contains("照片正在自动生成"),
                "an admitted favorite capture advances its status without another save click");
            feature.FavoritePhotoForTest=PortraitFavoritePhotoStatus.Available;feature.SampleLibrary();
            check(feature.FavoritesStatus=="收藏和照片已保存。",
                "favorite completion is announced only after its real photograph is durable");
            var entry=feature.Favorites[0];var saved=entry.Snapshot.ToJSON();
            var file=Path.Combine(root,"library","favorites","portrait-favorites-v1.json");
            var reopened=new PortraitFavoritesStore(file);
            check(reopened.Entries.Count==1&&reopened.Entries[0].Snapshot.ToJSON()==saved&&entry.SourceName=="收藏来源角色",
                "favorite complete snapshot and actual source name are durable before plugin unload");
            var captures=feature.Native.EditorCaptureCalls;
            for(var i=0;i<10;i++)check(feature.FavoriteAvailability(entry.ID)=="","compatible favorite reports available");
            check(feature.Native.EditorCaptureCalls==captures,"drawing favorite availability performs no native editor capture");
            feature.Native.Snapshot=feature.Native.Snapshot with{Customize=(byte[])feature.Native.Snapshot.Customize.Clone()};
            feature.Native.Snapshot.Customize[2]=90;feature.Sample();
            var applies=feature.Native.ApplyCalls;
            check(feature.FavoriteAvailability(entry.ID).Length>0,"favorite differing outside beautician scope is explicitly unavailable");
            feature.RestoreFavorite(entry.ID);
            check(feature.Native.ApplyCalls==applies&&feature.Native.Snapshot.Customize[2]==90,"unavailable favorite cannot partially change current editor");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("templates")))
        {
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);feature.Sample();feature.OpenPreview();
            var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();pages[4].Pieces[3]=new(100,1,1);
            feature.SetPlateObservations(pages);var before=feature.Native.Snapshot.Copy();var count=feature.Store.Count;var calls=feature.Native.ApplyCalls;
            feature.ApplyGlamourPlate(4);
            check(feature.Native.Snapshot.Look.Pieces[3]==new PortraitPiece(100,1,1)&&feature.Native.ApplyCalls==calls+1,"template switches all equipment in one native editor transaction");
            check(feature.Native.Snapshot.Customize.SequenceEqual(before.Customize)&&feature.Native.Snapshot.Composition==before.Composition&&feature.Store.Count==count+1,
                "template preserves appearance composition and creates one history record");
            var snapshot=feature.Native.Snapshot.ToJSON();count=feature.Store.Count;feature.Native.RejectApply=true;feature.ApplyGlamourPlate(0);
            check(feature.Native.Snapshot.ToJSON()==snapshot&&feature.Store.Count==count,"empty template leaves editor and history unchanged");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("template-playing-history")))
        {
            feature.EditComposition(c=>c with {BannerTimeline=1, PoseClassJob=0, AnimationProgress=.2f, Paused=true});
            feature.Sample();feature.OpenPreview();
            feature.EditComposition(c=>c with {BannerTimeline=0, PoseClassJob=1, AnimationProgress=.6f, Paused=false});
            feature.Sample(advanceMilliseconds:600);
            check(feature.Store.Count==1,"playing template source keeps the last frozen history record");
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);
            var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();
            pages[0].Pieces[3]=new(100,1,1);feature.SetPlateObservations(pages);
            feature.ApplyGlamourPlate(0);
            var latest=feature.Store.GetRecords(feature.Key!.Value).First();
            var recorded=PortraitEditorSnapshot.FromJSON(latest.SnapshotJSON)!;
            check(feature.Native.Snapshot.Composition is {BannerTimeline:0, PoseClassJob:1, Paused:false}
                && recorded.Composition is {BannerTimeline:0, PoseClassJob:1, Paused:false}
                && recorded.Look.Pieces[3]==new PortraitPiece(100,1,1),
                "template history and its photograph share the real playing editor state");
            feature.EditComposition(c=>c with {Paused=true,AnimationProgress=.8f});feature.Sample();feature.Sample(advanceMilliseconds:600);
            var paused=PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key.Value).First().SnapshotJSON)!;
            check(feature.Store.Count==3 && paused.Composition is {BannerTimeline:0, PoseClassJob:1, AnimationProgress:.8f, Paused:true},
                "pausing after a template records the newly frozen playing pose separately");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("favorite-playing-history")))
        {
            feature.EditComposition(c=>c with {BannerTimeline=1, Paused=true, AnimationProgress=.2f});
            feature.Sample();feature.OpenPreview();
            feature.EditComposition(c=>c with {BannerTimeline=0, Paused=false, AnimationProgress=.6f});
            feature.Sample();feature.SaveFavorite("播放中收藏",[]);feature.WaitFavorites();
            var favorite=feature.Favorites.Single();
            feature.EditComposition(c=>c with {BannerTimeline=1, Paused=true, AnimationProgress=.3f});
            feature.Sample();feature.Sample(advanceMilliseconds:600);
            feature.RestoreFavorite(favorite.ID);
            var restored=PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value).First().SnapshotJSON)!;
            check(feature.Native.Snapshot.Composition is {BannerTimeline:0, Paused:false, AnimationProgress:.6f}
                && restored.Composition is {BannerTimeline:0, Paused:false, AnimationProgress:.6f},
                "explicit favorite restoration retains the full playing snapshot in model and history");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("poll")))
        {
            feature.SetPlateNext(Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray());
            feature.SampleLibrary();var first=feature.GlamourPlateView;
            feature.SampleLibrary();feature.SampleLibrary();
            check(feature.PlateReader.Calls==1&&ReferenceEquals(first,feature.GlamourPlateView),"framework frames reuse a throttled detached template view");
            feature.MakePlatePollDue();feature.SampleLibrary();check(feature.PlateReader.Calls==2,"due framework polling can refresh saved templates");
            feature.Owner=new(11,21);feature.SampleLibrary();check(feature.PlateReader.Calls==3&&feature.GlamourPlateView!.Character!.Value.ContentID==21,
                "new character bypasses throttle and publishes its own unread template source");
            feature.Owner=null;feature.SampleLibrary();check(feature.GlamourPlateView is null&&feature.SourceGuideKey=="","logout immediately clears published character-bound source view");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("shared")))
        {
            feature.Sample();feature.OpenPreview();feature.EditComposition(c=>c with{Paused=true,CameraYaw=1,DirectionalRed=1,Frame=1,Decoration=1});feature.Sample();
            feature.ChangeAppearance("hair",2);feature.SaveFavorite("完整肖像",["肖像"]);feature.WaitFavorites();var favorite=feature.Favorites.Single();var original=favorite.Snapshot.Copy();
            feature.Owner=new(99,77);feature.Native.Snapshot=feature.Native.Snapshot with{EntryPoint=1,Customize=(byte[])feature.Native.Snapshot.Customize.Clone(),Composition=new(){BannerTimeline=1,Paused=true}};
            feature.Native.Snapshot.Customize[6]=1;feature.Sample();feature.SampleLibrary();var count=feature.Store.Count;
            check(feature.Favorites.Count==1&&feature.Favorites[0].SourceContentID==20&&feature.FavoriteAvailability(favorite.ID)=="",
                "favorites remain browsable and compatible across characters and editor entry points");
            feature.RestoreFavorite(favorite.ID);
            check(feature.Native.Snapshot.Customize[6]==2&&feature.Native.Snapshot.Composition==original.Composition&&feature.Native.Snapshot.EntryPoint==1,
                "favorite restore applies full beauty camera lighting and decoration to current editor entry point");
            var latest=feature.Store.GetRecordsForOwner(77).First();
            check(feature.Store.Count==count+1&&latest.Label=="恢复收藏：完整肖像"&&latest.Key.ContentID==77,
                "favorite restore appends exactly one current-character history record");
            check(feature.Favorites[0].Snapshot.ToJSON()==original.ToJSON(),"restore never rewrites source favorite provenance or snapshot");
            var calls=feature.Native.ApplyCalls;feature.Native.Snapshot=feature.Native.Snapshot with{GameBuild="different"};feature.Sample();var history=feature.Store.Count;
            check(feature.FavoriteAvailability(favorite.ID).Contains("版本"),"different build is explicitly unavailable instead of silently rebound");
            feature.RestoreFavorite(favorite.ID);check(feature.Native.ApplyCalls==calls&&feature.Store.Count==history,"different-build restoration performs no partial apply or history append");
            feature.RenameFavorite(favorite.ID,"重命名");feature.WaitFavorites();feature.TagFavorite(favorite.ID,["暖色","光"]);feature.WaitFavorites();
            var path=Path.Combine(root,"library","shared","portrait-favorites-v1.json");var stored=new PortraitFavoritesStore(path).Entries.Single();
            check(stored.Name=="重命名"&&stored.Tags.SequenceEqual(["暖色","光"]),"feature rename and tags are immediately durable");
            var priorMetadata=File.ReadAllBytes(path);
            feature.EditFavorite(favorite.ID,"一次编辑",["新标签","日常"]);feature.WaitFavorites();
            var edited=new PortraitFavoritesStore(path).Entries.Single();
            check(edited.Name=="一次编辑" && edited.Tags.SequenceEqual(["新标签","日常"])
                && priorMetadata.SequenceEqual(File.ReadAllBytes(path+".bak")),
                "one editor confirmation atomically persists both name and tags with a single prior-generation backup");
            var completeMetadata=File.ReadAllBytes(path);
            feature.EditFavorite(favorite.ID,"",["不得部分保存"]);feature.WaitFavorites();
            check(completeMetadata.SequenceEqual(File.ReadAllBytes(path)),
                "invalid combined metadata edit cannot partially save tags or rewrite the previous favorite");
            feature.RemoveFavorite(favorite.ID);feature.WaitFavorites();
            var trashPath=Path.Combine(Path.GetDirectoryName(path)!,"portrait-favorites-trash-v1.json");
            check(feature.Favorites.Count==0 && feature.FavoriteTrash.Single().ID==favorite.ID
                && new PortraitFavoritesStore(path).Entries.Single().ID==favorite.ID
                && new PortraitFavoritesTrashStore(trashPath).Entries.Single().ID==favorite.ID,
                "delete hides the favorite only after a durable complete tombstone while retaining its compatible source snapshot");
            feature.RenameFavorite(favorite.ID,"不得修改已删除项");feature.WaitFavorites();
            feature.TagFavorite(favorite.ID,["不得修改"]);feature.WaitFavorites();
            feature.EditFavorite(favorite.ID,"不得修改",["不得修改"]);feature.WaitFavorites();
            check(new PortraitFavoritesStore(path).Entries.Single().Name=="一次编辑"
                && new PortraitFavoritesStore(path).Entries.Single().Tags.SequenceEqual(["新标签","日常"])
                && feature.FavoriteTrash.Single().Name=="一次编辑",
                "stale metadata editor cannot mutate a deleted favorite or create a recovery conflict");
            feature.RestoreDeletedFavorite(favorite.ID);feature.WaitFavorites();
            check(feature.Favorites.Single().ID==favorite.ID && feature.Favorites.Single().Name=="一次编辑"
                && feature.FavoriteTrash.Count==0 && new PortraitFavoritesTrashStore(trashPath).Entries.Count==0,
                "restoring removes the tombstone durably without changing identity metadata or portrait");
            feature.RemoveFavorite(favorite.ID);feature.WaitFavorites();
        }
        using(var feature=new PortraitPreviewFeature(PathFor("shared")))
        {
            var path=Path.Combine(root,"library","shared","portrait-favorites-v1.json");
            var trashPath=Path.Combine(Path.GetDirectoryName(path)!,"portrait-favorites-trash-v1.json");
            check(feature.Favorites.Count==0 && feature.FavoriteTrash.Count==1,
                "restart reconstructs deleted visibility from durable tombstones instead of resurrecting source records");
            var deleted=feature.FavoriteTrash.Single();
            feature.RestoreDeletedFavorite(deleted.ID);feature.WaitFavorites();
            check(feature.Favorites.Single().ID==deleted.ID && feature.Favorites.Single().Snapshot.ToJSON()==deleted.Snapshot.ToJSON()
                && feature.FavoriteTrash.Count==0,
                "restarted trash can restore its exact complete snapshot and stable identity");
            feature.RemoveFavorite(deleted.ID);feature.WaitFavorites();
            feature.PermanentlyRemoveFavorite(deleted.ID);feature.WaitFavorites();
            check(feature.Favorites.Count==0 && feature.FavoriteTrash.Count==0
                && new PortraitFavoritesStore(path).Entries.Count==0 && new PortraitFavoritesTrashStore(trashPath).Entries.Count==0,
                "permanent deletion removes source and tombstone durably after explicit trash action");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("shared")))
            check(feature.Favorites.Count==0 && feature.FavoriteTrash.Count==0,
                "permanently deleted favorites cannot return after a second restart");
        using(var feature=new PortraitPreviewFeature(PathFor("trash-write-failure")))
        {
            feature.Sample();feature.OpenPreview();feature.SaveFavorite("安全恢复",["原标签"]);feature.WaitFavorites();
            var favorite=feature.Favorites.Single();
            var path=Path.Combine(root,"library","trash-write-failure","portrait-favorites-v1.json");
            var trashPath=Path.Combine(Path.GetDirectoryName(path)!,"portrait-favorites-trash-v1.json");
            feature.RemoveFavorite(favorite.ID);feature.WaitFavorites();
            feature.RestoreDeletedFavorite(favorite.ID);feature.WaitFavorites();
            var before=File.ReadAllBytes(path);
            using(var blocked=new FileStream(trashPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                feature.RemoveFavorite(favorite.ID);feature.WaitFavorites();
                check(feature.Favorites.Single().ID==favorite.ID && feature.FavoriteTrash.Count==0
                    && before.SequenceEqual(File.ReadAllBytes(path)),
                    "failed tombstone publication cannot hide an active favorite or change its compatible source document");
            }
            feature.RemoveFavorite(favorite.ID);feature.WaitFavorites();
            using(var blocked=new FileStream(trashPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                feature.RestoreDeletedFavorite(favorite.ID);feature.WaitFavorites();
                check(feature.Favorites.Count==0 && feature.FavoriteTrash.Single().ID==favorite.ID
                    && new PortraitFavoritesStore(path).Entries.Single().ID==favorite.ID,
                    "failed tombstone removal never rolls back by deleting the existing valid favorite source");
            }
            using(var blocked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                feature.PermanentlyRemoveFavorite(favorite.ID);feature.WaitFavorites();
                check(feature.FavoriteTrash.Single().ID==favorite.ID
                    && new PortraitFavoritesStore(path).Entries.Single().ID==favorite.ID,
                    "failed permanent source removal leaves the entire favorite recoverable in trash");
            }
            using(var blocked=new FileStream(trashPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                feature.PermanentlyRemoveFavorite(favorite.ID);feature.WaitFavorites();
                check(feature.Favorites.Count==0 && feature.FavoriteTrash.Single().ID==favorite.ID
                    && new PortraitFavoritesStore(path).Entries.Count==0
                    && new PortraitFavoritesTrashStore(trashPath).Entries.Single().Snapshot.ToJSON()==favorite.Snapshot.ToJSON(),
                    "interrupted permanent cleanup retains a complete durable tombstone after the source is already removed");
            }
            feature.RestoreDeletedFavorite(favorite.ID);feature.WaitFavorites();
            check(feature.Favorites.Single().ID==favorite.ID && feature.FavoriteTrash.Count==0
                && new PortraitFavoritesStore(path).Entries.Single().Name=="安全恢复",
                "a partial permanent cleanup can recover by restoring its complete tombstone to the source library");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("atomic")))
        {
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);feature.Sample();feature.OpenPreview();
            var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();pages[0].Pieces[3]=new(100,1,1);feature.SetPlateObservations(pages);
            var before=feature.Native.Snapshot.ToJSON();var count=feature.Store.Count;var calls=feature.Native.ApplyCalls;
            feature.Native.RejectApply=true;feature.ApplyGlamourPlate(0);
            check(feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count&&feature.Native.ApplyCalls==calls,"native refusal preserves full editor and history cursor");
            feature.Native.RejectApply=false;feature.Native.CanBlockSave=false;feature.ApplyGlamourPlate(0);
            check(feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count,"missing save guard rejects library preview before native mutation");
            feature.Native.CanBlockSave=true;feature.Native.AfterEditorApply=()=>feature.Store.Dispose();feature.ApplyGlamourPlate(0);
            check(feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count,"history commit failure rolls the full editor back after native application");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("player-native-failure")))
        {
            const string technical="Native editor credential baseline unsafe. InternalException 0x1234";
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);feature.Sample();feature.OpenPreview();
            var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();pages[0].Pieces[3]=new(100,1,1);feature.SetPlateObservations(pages);
            feature.Native.RejectApply=true;feature.Native.LastError=technical;
            feature.ApplyGlamourPlate(0);
            check(feature.StatusIsError && !feature.Status.Contains(technical) && feature.Native.LastError==technical,
                "template failure keeps native technical evidence out of player status");
            feature.ChangeAppearance("hair",2);
            check(feature.StatusIsError && !feature.Status.Contains(technical) && feature.Native.LastError==technical,
                "beauty failure keeps native technical evidence out of player status");
            feature.Native.RejectApply=false;feature.ChangeAppearance("hair",2);
            feature.Native.RejectApply=true;feature.Undo();
            check(feature.StatusIsError && !feature.Status.Contains(technical) && feature.Native.LastError==technical,
                "history restore failure keeps native technical evidence out of player status");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("stale-plate-owner")))
        {
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);feature.Sample();feature.OpenPreview();var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();pages[0].Pieces[3]=new(100,1,1);feature.SetPlateObservations(pages);
            var calls=feature.Native.ApplyCalls;var before=feature.Native.Snapshot.ToJSON();var count=feature.Store.Count;feature.Owner=new(99,88);feature.ApplyGlamourPlate(0);
            check(feature.Native.ApplyCalls==calls&&feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count,"character change invalidates previous template publication before polling catches up");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("quota"),1))
        {
            feature.SetLibraryValidation([new(100,1<<3,1,2)]);feature.Sample();feature.OpenPreview();var pages=Enumerable.Range(0,20).Select(i=>new PortraitGlamourPlate(i,new PortraitPiece[12])).ToArray();pages[0].Pieces[3]=new(100,1,1);feature.SetPlateObservations(pages);
            var calls=feature.Native.ApplyCalls;var before=feature.Native.Snapshot.ToJSON();feature.ApplyGlamourPlate(0);
            check(feature.Native.ApplyCalls==calls&&feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==0,"history quota is checked before attempting a template native mutation");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("sources")))
        {
            feature.Sample();feature.OpenPreview();var inventory=new OwnedSourceKey(OwnedSourceKind.Inventory);var dresser=new OwnedSourceKey(OwnedSourceKind.GlamourDresser);var retainer=new OwnedSourceKey(OwnedSourceKind.RetainerInventory,987654321);
            OwnedItemVariant Variant(OwnedSourceKey source,byte first,byte second)=>new(source,100,1,0,1,first,second,0,false,OwnedReadStatus.Current);
            feature.SetOwnedItems(new(feature.Owner,[new(100,[Variant(inventory,0,0),Variant(dresser,1,1),Variant(retainer,0,1)])],[]),PortraitSlot.Body,[new(100,"装备",1,2,true,"")]);
            feature.SetRetainerName(987654321,"米米");var count=feature.Store.Count;feature.EquipFromSources(PortraitSlot.Body,100,[OwnedSourceKind.GlamourDresser]);
            check(feature.Native.Snapshot.Look.Pieces[3]==new PortraitPiece(100,1,1)&&feature.Store.Count==count+1,"selected source controls native preview dye variant and produces one edit");
            var locations=feature.StorageLocations(100);
            check(locations.Contains("背包")&&locations.Contains("投影台")&&locations.Contains("雇员背包 · 米米")&&!locations.Any(value=>value.Contains("987654321")),
                "equipment detail lists readable storage names without exposing retainer identifiers");
            var before=feature.Native.Snapshot.ToJSON();count=feature.Store.Count;feature.EquipFromSources(PortraitSlot.Body,100,[OwnedSourceKind.Armoire]);
            check(feature.Native.Snapshot.ToJSON()==before&&feature.Store.Count==count,"unread selected source cannot silently use an unrelated inventory copy");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("owned-immediate")))
        {
            var source=new OwnedSourceKey(OwnedSourceKind.RetainerInventory,7);feature.ObserveOwned(source,[new(100,1,0,1,1,1)]);
            check(feature.FlushOwned(),"feature persistence gate acknowledges accepted owned-cache write without waiting thirty seconds");
            var path=Path.Combine(root,"library","owned-immediate","portrait-owned-cache-v1.json");var catalog=new OwnedEquipmentCatalog(id=>id);var file=new OwnedEquipmentCacheFile(Path.GetDirectoryName(path)!);
            check(file.Load(catalog,out _),"owned inventory is durable while the feature is still open");catalog.ActivateCharacter(feature.Owner!.Value);
            check(catalog.BuildView(DateTimeOffset.Now).Items.Single().Variants.Single().Stain2==1,"immediate owned write includes full selected source dual-dye record");
            var writes=feature.OwnedWrites;feature.Advance(100);feature.ObserveOwned(source,[new(100,1,0,1,1,1)]);feature.FlushOwned();
            check(feature.OwnedWrites==writes,"unchanged feature capture refresh does not rewrite durable owned content");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("equipment-owner-publication")))
        {
            var originalOwner=feature.Owner;
            var retainer=new OwnedSourceKey(OwnedSourceKind.RetainerInventory,123);
            var source=new OwnedSourceView(retainer,OwnedReadStatus.Current,DateTimeOffset.UtcNow,1,"");
            var oldView=new OwnedEquipmentView(originalOwner,[new(100,[new(retainer,100,1,0,1,0,0,0,false,OwnedReadStatus.Current)])],[source]);
            feature.SetOwnedItems(oldView,PortraitSlot.Body,[new(100,"原角色独占装备",1,2,true,"")]);
            feature.SetOwnedItems(oldView,PortraitSlot.Facewear,[new(71,"原角色解锁眼镜",1,0,true,"")]);
            feature.SetRetainerName(123,"原角色雇员");
            var ownerReads=feature.NativeOwnerReads;
            _=feature.GetItems(PortraitSlot.Body);_=feature.Sources;_=feature.EquippedItems;
            _=feature.SourceCharacter;_=feature.RetainerRoster;_=feature.StorageLocations(100);
            check(feature.NativeOwnerReads==ownerReads,
                "drawing published equipment and source details never samples the live native character");
            check(feature.GetItems(PortraitSlot.Body).Single().ID==100 && feature.GetItems(PortraitSlot.Facewear).Single().ID==71,
                "current owner keeps both owned equipment and its unlocked facewear publication");
            check(feature.StorageLocations(100).Single()=="雇员背包 · 原角色雇员",
                "current owner sees the readable retainer location from its own detached inventory");
            feature.Owner=new(99,88);
            check(feature.SourceCharacter==originalOwner && feature.GetItems(PortraitSlot.Body).Count==1 && feature.NativeOwnerReads==ownerReads,
                "source getters remain on the published Framework snapshot between updates");
            feature.SampleSourceOwner();
            check(feature.GetItems(PortraitSlot.Body).Count==0 && feature.GetItems(PortraitSlot.Facewear).Count==0,
                "character switch hides prior equipment and unlocked facewear before inventory polling");
            check(feature.StorageLocations(100).Count==0,
                "character switch hides prior equipment locations before inventory polling");
            feature.Owner=null;
            feature.SampleSourceOwner();
            check(feature.GetItems(PortraitSlot.Body).Count==0 && feature.GetItems(PortraitSlot.Facewear).Count==0 && feature.StorageLocations(100).Count==0,
                "logout cannot expose prior equipment, unlocks, or retainer locations");
            feature.Owner=originalOwner;
            feature.SampleSourceOwner();
            check(feature.GetItems(PortraitSlot.Body).Single().ID==100 && feature.GetItems(PortraitSlot.Facewear).Single().ID==71 && feature.StorageLocations(100).Count==1,
                "owner checks preserve the detached publication without clearing valid cached equipment or unlocks");
            feature.Owner=new(99,88);
            feature.SampleSourceOwner();
            var inventory=new OwnedSourceKey(OwnedSourceKind.Inventory);
            var newView=new OwnedEquipmentView(feature.Owner,[new(200,[new(inventory,200,1,0,1,0,0,0,false,OwnedReadStatus.Current)])],[]);
            feature.SetOwnedItems(newView,PortraitSlot.Body,[new(200,"新角色装备",1,2,true,"")]);
            feature.SetOwnedItems(newView,PortraitSlot.Facewear,[new(72,"新角色解锁眼镜",1,0,true,"")]);
            check(feature.GetItems(PortraitSlot.Body).Single().ID==200 && feature.GetItems(PortraitSlot.Facewear).Single().ID==72,
                "refreshed current-owner publication exposes its own equipment and unlocked facewear");
            check(feature.StorageLocations(200).Single()=="背包" && feature.StorageLocations(100).Count==0,
                "refreshed current-owner locations exclude the previous character item and retainer");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("retainer-roster-owner")))
        {
            var owner=feature.Owner!.Value;var readAt=DateTimeOffset.UtcNow;
            feature.SetOwnedItems(new(owner,[],[new(new(OwnedSourceKind.RetainerInventory,11),OwnedReadStatus.Current,readAt,3,"")]),PortraitSlot.Body,[]);
            feature.SetRetainerRoster(new(owner,new ulong[]{11,22},OwnedReadStatus.Current));
            check(feature.SourceCharacter==owner&&feature.RetainerRoster?.RetainerIDs.Count==2&&feature.Sources.Count==1,"published roster and inventory sources share the current runtime character");
            feature.Owner=new(99,88);
            feature.SampleSourceOwner();
            check(feature.RetainerRoster is null&&feature.Sources.Count==0&&feature.SourceCharacter==feature.Owner,"character switch hides prior source rows and roster before throttled native inventory polling");
            feature.Owner=null;
            feature.SampleSourceOwner();
            check(feature.RetainerRoster is null&&feature.Sources.Count==0&&feature.SourceCharacter is null,"logout cannot expose previous retainer denominator or source rows");
        }
        using(var feature=new PortraitPreviewFeature(PathFor("owned-write-failure")))
        {
            var path=Path.Combine(root,"library","owned-write-failure","portrait-owned-cache-v1.json");Directory.CreateDirectory(path);
            var source=new OwnedSourceKey(OwnedSourceKind.Inventory);feature.ObserveOwned(source,[new(100,1,0)]);
            check(!feature.FlushOwned(),"owned write failure is observable before feature disposal");feature.SampleLibrary();
            check(feature.Status.Contains("读取记录未能保存"),"asynchronous cache failure reaches player-visible feature status");
            Directory.Delete(path);feature.Advance(100);feature.MakeStorageObservationDue();feature.ObserveOwned(source,[new(100,1,0)]);feature.WaitStorageObservation();check(feature.FlushOwned(),"unchanged content can recover after storage becomes writable");feature.SampleLibrary();
            check(!feature.Status.Contains("读取记录未能保存"),"successful cache recovery clears the player-visible persistence warning");
        }
    }
}
