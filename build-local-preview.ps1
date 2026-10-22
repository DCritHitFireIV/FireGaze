param(
    [string]$SqpackPath = '',
    [string]$HmsCompatRoot = '',
    [string]$PortraitFrozenSdkPath = '',
    [switch]$RequireFullValidation
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previewRoot = $PSScriptRoot
$previewCompletedSuites = 0
$previewSkippedSuites = [System.Collections.Generic.List[string]]::new()
if (!$env:DALAMUD_HOME) {
    $env:DALAMUD_HOME = Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'
}

# Prefer an explicit client data path. Otherwise use the launcher's configured
# installation; never silently use a hardcoded path for data-sensitive tests.
if ($SqpackPath) {
    if (!(Test-Path -LiteralPath $SqpackPath -PathType Container)) {
        throw "SqpackPath does not exist: $SqpackPath"
    }
} else {
    $previewLauncherPath = Join-Path $env:APPDATA 'XIVLauncherCN\launcherConfigV3.json'
    if (Test-Path -LiteralPath $previewLauncherPath -PathType Leaf) {
        $previewLauncher = Get-Content -Raw -LiteralPath $previewLauncherPath | ConvertFrom-Json
        if ($previewLauncher.GamePath) {
            $previewCandidateSqpack = Join-Path $previewLauncher.GamePath 'game\sqpack'
            if (Test-Path -LiteralPath $previewCandidateSqpack -PathType Container) {
                $SqpackPath = $previewCandidateSqpack
            }
        }
    }
}

# This checks a historical projection contract only. The isolated source fixture
# is not a production dependency and is never deployed into the game.
if ($HmsCompatRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $HmsCompatRoot 'Services\LocalPreviewProjection.cs') -PathType Leaf)) {
        throw "HmsCompatRoot does not contain the isolated contract fixture: $HmsCompatRoot"
    }
} else {
    $previewCandidateCompat = Join-Path (Split-Path $previewRoot -Parent) 'hms-local-preview-compat'
    if (Test-Path -LiteralPath (Join-Path $previewCandidateCompat 'Services\LocalPreviewProjection.cs') -PathType Leaf) {
        $HmsCompatRoot = $previewCandidateCompat
    }
}
if ($RequireFullValidation -and (!$SqpackPath -or !$HmsCompatRoot)) {
    throw 'Full validation requires the current CN SqpackPath and the isolated HmsCompatRoot contract fixture.'
}
if ($PortraitFrozenSdkPath -and !(Test-Path -LiteralPath $PortraitFrozenSdkPath -PathType Leaf)) {
    throw "PortraitFrozenSdkPath does not exist: $PortraitFrozenSdkPath"
}

function Invoke-PreviewSuite([string]$Suite, [string[]]$SuiteArguments = @()) {
    Write-Output "Running offline suite: $Suite"
    $previewDotnetArgs = @('run', '--project', (Join-Path $previewRoot "tools\$Suite\$Suite.csproj"), '-c', 'Release') + $SuiteArguments
    & dotnet @previewDotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $script:previewCompletedSuites++
}

foreach ($previewSuite in @('LocalPreviewTests', 'PreviewRuntimeBoundaryTests', 'GroundedPreviewMotionTests', 'NativePreviewActorTests', 'NativeDismountTests', 'HmsShadowTests', 'HmsOriginalAdapterTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PluginAsyncLifecycleTests', 'ActivityLogLifecycleTests', 'PluginAsyncIntegrationTests')) {
    Invoke-PreviewSuite $previewSuite
}
Invoke-PreviewSuite 'LocalPreviewPreferencesTests'
# Exact official metadata plus fake native boundaries; never installs a live Hook.
Invoke-PreviewSuite 'ReviewedSafetyHookFactoryTests'
Invoke-PreviewSuite 'IconReuseTests'
foreach ($previewSuite in @('PortraitPreviewTests', 'PortraitNativeTests', 'PortraitOwnedTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PortraitInventoryRefreshTests', 'PortraitRuntimeSafetyTests', 'NearbyWindowTests')) {
    Invoke-PreviewSuite $previewSuite
}
Invoke-PreviewSuite 'PortraitSafetySnapshotTests'
Invoke-PreviewSuite 'PortraitEditorOpeningReceiptTests'
Invoke-PreviewSuite 'PortraitEditorOpeningNativeTests'
Invoke-PreviewSuite 'PortraitStorageBudgetTests'
Invoke-PreviewSuite 'PortraitStorageIntegrationTests'
foreach ($previewSuite in @('PortraitEquipmentFilterTests', 'PortraitFavoritesTests', 'PortraitFavoritePhotoTests', 'PortraitGlamourPlateTests', 'PortraitLibraryPresentationTests', 'PortraitAnalysisTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PortraitComparisonTests', 'PortraitLightDiagramTests', 'PortraitLightingSpaceTests', 'PortraitObserverCameraTests', 'PortraitCameraFitTests', 'PortraitAnalysisWindowTests', 'PortraitLightIntegrationTests', 'PortraitLightNativeTests', 'PortraitLightingFitTests', 'PortraitLightingFitSessionTests', 'PortraitSampleRegionTests', 'PortraitCaptureTests', 'PortraitNativeLiveViewTests', 'PortraitSoftwareCompositionTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PortraitLightingNativeTests', 'PortraitLightingAdapterTests', 'PortraitLightingBindingTests', 'PortraitLightingCaptureTests', 'PortraitLightingControlsTests', 'PortraitLightingFeatureBackendTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PortraitEditorStateTests', 'PortraitEditingTests', 'PortraitOperationLabelTests', 'PortraitHistoryTests', 'PortraitHistoryPerformanceTests', 'PortraitThumbnailTests', 'PortraitThumbnailPipelineTests', 'PortraitThumbnailSourceTests', 'PortraitHistoryPhotoIntegrationTests', 'PortraitPlateMirrorTests', 'PortraitFeatureIntegrationTests', 'PortraitVisualLayoutTests', 'PortraitPopupLayoutTests', 'VisualSystemTests')) {
    Invoke-PreviewSuite $previewSuite
}
# Detached spatial-scene lifecycle contract. This suite intentionally has no
# Dalamud/native dependency; keep it in the full offline gate so stale callback
# and ownership regressions fail before any host assembly is built or deployed.
Invoke-PreviewSuite 'PortraitSpatialSceneTests'
# Compile the actual native arbitration and owned portrait adapters against the
# current SDK. These suites exercise call ordering and retirement with native
# boundaries substituted; they do not install hooks or create a live game scene.
Invoke-PreviewSuite 'PortraitOwnedSceneHookTests'
Invoke-PreviewSuite 'PortraitOwnedEntryProofTests'
Invoke-PreviewSuite 'PortraitOwnedSceneTests'
Invoke-PreviewSuite 'PortraitOwnedTextureTests'
Invoke-PreviewSuite 'PortraitOwnedSceneControllerTests'
Invoke-PreviewSuite 'PortraitOwnedSceneRenderTargetTests'
Invoke-PreviewSuite 'PortraitOwnedSceneSlotTests'
Invoke-PreviewSuite 'PortraitOwnedScenePhaseIntegrationTests'
Invoke-PreviewSuite 'PortraitOwnedSceneContextLifetimeTests'
Invoke-PreviewSuite 'PortraitNativePhaseObservationTests'
Invoke-PreviewSuite 'PortraitNativePhaseHostTests'
# Map a disposable no-execute image in the test process only; never load Boot,
# invoke its exports, attach to the game, or install a native hook.
Invoke-PreviewSuite 'PortraitNativeBootImageTests' @('--', (Join-Path $env:DALAMUD_HOME 'Dalamud.Boot.dll'))

if ($SqpackPath) {
    # Frozen SDK bytes are disk-only historical fixtures, never loaded/deployed.
    # Current unsupported SDK refusal is asserted separately in each suite.
    $previewPortraitDiskArgs = @('--', '--game-exe', (Join-Path (Split-Path $SqpackPath -Parent) 'ffxiv_dx11.exe'))
    if ($PortraitFrozenSdkPath) { $previewPortraitDiskArgs += @('--frozen-sdk', $PortraitFrozenSdkPath) }
    # Read the actual configured client's PE only; this suite never opens its process.
    Invoke-PreviewSuite 'PortraitCounterIngressTests' $previewPortraitDiskArgs
    Invoke-PreviewSuite 'PortraitResourceIngressTests' $previewPortraitDiskArgs
    Invoke-PreviewSuite 'PortraitNativePhaseResolverTests' $previewPortraitDiskArgs
    Invoke-PreviewSuite 'NativeTimelineQualificationTests' @('--', '--game-exe', (Join-Path (Split-Path $SqpackPath -Parent) 'ffxiv_dx11.exe'), '--boot-dll', (Join-Path $env:DALAMUD_HOME 'Dalamud.Boot.dll'))
    Invoke-PreviewSuite 'NativeResolvedTimelineTests' @('--', '--sqpack', $SqpackPath)
    foreach ($previewSuite in @('PortraitAppearanceTests', 'PortraitValidationTests')) {
        Invoke-PreviewSuite $previewSuite @('--', '--sqpack', $SqpackPath)
    }
    Invoke-PreviewSuite 'GposeEmoteTests' @('--', $SqpackPath)
    foreach ($previewSuite in @('TrialSpecialHotbarTests', 'TrialSpecialRuntimeTests', 'NativeCompatibilityTests', 'NativeCollectionTransitionTests', 'NativeEmoteTests')) {
        Invoke-PreviewSuite $previewSuite @('--', $SqpackPath)
    }
    Invoke-PreviewSuite 'LocalPreviewInteractionTests' @('--', '--sqpack', $SqpackPath)
} else {
    $previewSkippedSuites.Add('PortraitCounterIngressTests')
    $previewSkippedSuites.Add('PortraitResourceIngressTests')
    $previewSkippedSuites.Add('PortraitNativePhaseResolverTests')
    $previewSkippedSuites.Add('NativeTimelineQualificationTests')
    $previewSkippedSuites.Add('NativeResolvedTimelineTests')
    foreach ($previewSuite in @('PortraitAppearanceTests', 'PortraitValidationTests')) {
        Invoke-PreviewSuite $previewSuite
    }
    Invoke-PreviewSuite 'GposeEmoteTests'
    Invoke-PreviewSuite 'LocalPreviewInteractionTests'
    foreach ($previewSuite in @('TrialSpecialHotbarTests', 'TrialSpecialRuntimeTests', 'NativeCompatibilityTests', 'NativeCollectionTransitionTests', 'NativeEmoteTests')) {
        $previewSkippedSuites.Add($previewSuite)
    }
    Write-Warning 'No current CN SqpackPath: native phase/timeline and five data-dependent suites were not run; GPose/item/portrait game-data coverage is incomplete.'
}

if ($HmsCompatRoot) {
    Invoke-PreviewSuite 'PreviewBridgeContractTests' @("-p:HmsCompatRoot=$HmsCompatRoot")
} else {
    $previewSkippedSuites.Add('PreviewBridgeContractTests')
    Write-Warning 'No isolated HmsCompatRoot fixture: historical bridge contract suite was not run.'
}

# These last three suites inspect the freshly built host assembly and payload.
& dotnet build (Join-Path $previewRoot 'src\FireGaze\FireGaze.csproj') -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($previewSuite in @('ClientWorkflowTests', 'InstallerSettingsTests', 'UITextSafetyTests', 'ArtifactPortabilityTests')) {
    Invoke-PreviewSuite $previewSuite
}
Invoke-PreviewSuite 'PortraitWindowLifecycleTests'
Invoke-PreviewSuite 'PluginPhysicalDrainTests'
Invoke-PreviewSuite 'PortraitStorageObservationDrainTests'

Write-Output "Offline validation finished: $previewCompletedSuites suites; skipped: $($previewSkippedSuites -join ', ')."
if (!$SqpackPath) { Write-Output 'Current-client GPose, item-data, portrait appearance, and portrait validation coverage is incomplete.' }
Write-Output 'Build and offline checks do not prove in-game acceptance.'
exit 0
