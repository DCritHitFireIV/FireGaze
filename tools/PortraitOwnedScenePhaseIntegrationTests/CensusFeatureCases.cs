#if !CENSUS_FEATURE
internal static class CensusFeatureCases
{
    internal static int Run() => throw new InvalidOperationException("FAIL: the production Feature source-census command/lifecycle partial is not connected.");
}
#else
using FireGaze.PortraitPreview;
using System.Runtime.InteropServices;

internal static class CensusFeatureCases
{
    internal static int Run()
    {
        using var fixture = new CensusFeatureBoundary();
        var feature = fixture.Feature;
        feature.OwnedViewportBusy = true;
        if (!feature.SourceCensusBlocksOperations)
            throw new InvalidOperationException("FAIL: an active independent scene must suppress automatic photos and competing image operations.");
        feature.OwnedViewportBusy = false;
        if (!feature.CensusCommand("native-census", "status") || fixture.Factory.Creates != 0)
            throw new InvalidOperationException("FAIL: actual Feature status must remain passive.");
        if (!feature.CensusSubscribed) throw new InvalidOperationException("FAIL: actual Feature must subscribe its native lifetime revocation.");
        if (!feature.CensusCommand("native-census", "start") || fixture.Factory.Creates != 8)
            throw new InvalidOperationException("FAIL: actual Feature start must install the single diagnostic batch.");
        fixture.Source(0); feature.CensusTick(); fixture.Source(1); feature.CensusTick();
        if (feature.CensusSnapshot?.Result is not { Complete: true, Rows.Count: 8 })
            throw new InvalidOperationException("FAIL: actual Feature tick must publish the controller's one eight-slot result.");
        if (!feature.CensusJSON().Contains("\"productionAdmission\":false", StringComparison.Ordinal))
            throw new InvalidOperationException("FAIL: actual Feature IPC projection must serialize native values as detached JSON.");
        if (!feature.CensusCommand("native-census", "start")) throw new InvalidOperationException("FAIL: explicit second Feature attempt.");
        fixture.Source(0); feature.CensusTick(); feature.CensusFinalize(); fixture.Source(1); feature.CensusTick();
        if (feature.CensusSnapshot?.Result is not null || feature.CensusActive)
            throw new InvalidOperationException("FAIL: actual Feature native-finalize callback must revoke before a later return.");
        feature.CensusCommand("native-census", "start"); fixture.Source(0); feature.CensusTick();
        feature.CensusMutation(); fixture.Source(1); feature.CensusTick();
        if (feature.CensusSnapshot?.Result is not null || feature.CensusActive)
            throw new InvalidOperationException("FAIL: actual Feature mutation must revoke its session attempt.");
        feature.CensusCommand("native-census", "start"); fixture.Source(0); feature.CensusTick();
        feature.CensusLogout(); fixture.Source(1); feature.CensusTick();
        if (feature.CensusSnapshot?.Result is not null || feature.CensusActive)
            throw new InvalidOperationException("FAIL: actual Feature logout must revoke its session attempt.");
        feature.CensusCommand("native-census", "start"); fixture.Source(0); feature.CensusTick();
        feature.CensusTerritory(); fixture.Source(1); feature.CensusTick();
        if (feature.CensusSnapshot?.Result is not null || feature.CensusActive)
            throw new InvalidOperationException("FAIL: actual Feature territory event must revoke its session attempt.");
        feature.CensusCommand("native-census", "start"); feature.CensusDispose();
        if (!feature.CensusDrain.Wait(TimeSpan.FromSeconds(5)) || feature.CensusActive || feature.CensusSubscribed)
            throw new InvalidOperationException("FAIL: actual Feature dispose must return a confirmed controller retirement task.");
        Console.WriteLine("CensusFeatureCases: actual production partial start/status/tick/PreFinalize/mutation/logout/Dispose passed.");
        return 0;
    }
}
#endif
