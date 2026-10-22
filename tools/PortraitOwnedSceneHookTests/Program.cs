using FireGaze.PortraitPreview;

#if HAS_OWNED_HOOKS
var experimentResult = OwnedExperimentBrokerCases.Run();
if (experimentResult != 0) return experimentResult;
var brokerResult = OwnedControllerBrokerCases.Run();
if (brokerResult != 0) return brokerResult;
var jointHostResult = OwnedJointHostCases.Run();
if (jointHostResult != 0) return jointHostResult;
var jointPlanResult = OwnedJointPlanCases.Run();
if (jointPlanResult != 0) return jointPlanResult;
var consumerEventResult = OwnedPassiveConsumerEventsCases.Run();
if (consumerEventResult != 0) return consumerEventResult;
var gatewayResult = OwnedGatewayCases.Run();
if (gatewayResult != 0) return gatewayResult;
var physicalResult = OwnedPhysicalCases.Run();
if (physicalResult != 0) return physicalResult;
var eventResult = OwnedPassiveScalarEventCases.Run();
if (eventResult != 0) return eventResult;
var retentionResult = OwnedDrainRetentionCases.Run();
if (retentionResult != 0) return retentionResult;
var result = HookCases.Run();
if (result == 0) result = OwnedSceneIntegrationCases.Run();
return result == 0 ? OwnedReservationCases.Run() : result;
#else
Console.Error.WriteLine("FAIL native portrait arbitration is absent: source Render cannot issue a phase-bound lease or retire before native takeover");
return 1;
#endif
