using FireGaze.PortraitPreview;

#if HAS_OWNED_HOOKS
var gatewayResult = OwnedGatewayCases.Run();
if (gatewayResult != 0) return gatewayResult;
var physicalResult = OwnedPhysicalCases.Run();
if (physicalResult != 0) return physicalResult;
var result = HookCases.Run();
if (result == 0) result = OwnedSceneIntegrationCases.Run();
return result == 0 ? OwnedReservationCases.Run() : result;
#else
Console.Error.WriteLine("FAIL native portrait arbitration is absent: source Render cannot issue a phase-bound lease or retire before native takeover");
return 1;
#endif
