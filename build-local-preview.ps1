param([string]$SqpackPath = '')

$ErrorActionPreference = 'Stop'
$previewRoot = $PSScriptRoot
$env:DALAMUD_HOME = Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'

foreach ($previewSuite in @('LocalPreviewTests', 'PreviewRuntimeBoundaryTests', 'HmsShadowTests', 'HmsOriginalAdapterTests')) {
    dotnet run --project (Join-Path $previewRoot "tools\$previewSuite\$previewSuite.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($SqpackPath) {
    dotnet run --project (Join-Path $previewRoot 'tools\LocalPreviewInteractionTests\LocalPreviewInteractionTests.csproj') -c Release -- --sqpack $SqpackPath
} else {
    dotnet run --project (Join-Path $previewRoot 'tools\LocalPreviewInteractionTests\LocalPreviewInteractionTests.csproj') -c Release
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build (Join-Path $previewRoot 'src\FireGaze\FireGaze.csproj') -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($previewSuite in @('ClientWorkflowTests', 'UITextSafetyTests')) {
    dotnet run --project (Join-Path $previewRoot "tools\$previewSuite\$previewSuite.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
