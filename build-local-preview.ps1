param(
    [string]$SqpackPath = '',
    [string]$HmsCompatRoot = '',
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
Invoke-PreviewSuite 'LocalPreviewPreferencesTests'
foreach ($previewSuite in @('PortraitPreviewTests', 'PortraitNativeTests', 'PortraitOwnedTests')) {
    Invoke-PreviewSuite $previewSuite
}
foreach ($previewSuite in @('PortraitEditorStateTests', 'PortraitEditingTests', 'PortraitHistoryTests', 'PortraitHistoryPerformanceTests', 'PortraitPlateMirrorTests', 'PortraitFeatureIntegrationTests', 'PortraitVisualLayoutTests', 'VisualSystemTests')) {
    Invoke-PreviewSuite $previewSuite
}

if ($SqpackPath) {
    foreach ($previewSuite in @('PortraitAppearanceTests', 'PortraitValidationTests')) {
        Invoke-PreviewSuite $previewSuite @('--', '--sqpack', $SqpackPath)
    }
    Invoke-PreviewSuite 'GposeEmoteTests' @('--', $SqpackPath)
    foreach ($previewSuite in @('TrialSpecialHotbarTests', 'TrialSpecialRuntimeTests', 'NativeCompatibilityTests', 'NativeEmoteTests')) {
        Invoke-PreviewSuite $previewSuite @('--', $SqpackPath)
    }
    Invoke-PreviewSuite 'LocalPreviewInteractionTests' @('--', '--sqpack', $SqpackPath)
} else {
    foreach ($previewSuite in @('PortraitAppearanceTests', 'PortraitValidationTests')) {
        Invoke-PreviewSuite $previewSuite
    }
    Invoke-PreviewSuite 'GposeEmoteTests'
    Invoke-PreviewSuite 'LocalPreviewInteractionTests'
    foreach ($previewSuite in @('TrialSpecialHotbarTests', 'TrialSpecialRuntimeTests', 'NativeCompatibilityTests', 'NativeEmoteTests')) {
        $previewSkippedSuites.Add($previewSuite)
    }
    Write-Warning 'No current CN SqpackPath: four data-dependent suites and the GPose/item/portrait game-data extensions were not run; portrait appearance/validation ran their base checks only.'
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
foreach ($previewSuite in @('ClientWorkflowTests', 'UITextSafetyTests', 'ArtifactPortabilityTests')) {
    Invoke-PreviewSuite $previewSuite
}

Write-Output "Offline validation finished: $previewCompletedSuites/31 suites; skipped: $($previewSkippedSuites -join ', ')."
if (!$SqpackPath) { Write-Output 'Current-client GPose, item-data, portrait appearance, and portrait validation coverage is incomplete.' }
Write-Output 'Build and offline checks do not prove in-game or two-client acceptance.'
exit 0
