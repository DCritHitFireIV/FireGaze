param([string]$SqpackPath = '')

$ErrorActionPreference = 'Stop'
$previewRoot = $PSScriptRoot
$env:DALAMUD_HOME = Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'

dotnet run --project (Join-Path $previewRoot 'tools\LocalPreviewTests\LocalPreviewTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $previewRoot 'tools\PreviewRuntimeBoundaryTests\PreviewRuntimeBoundaryTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($SqpackPath) {
    dotnet run --project (Join-Path $previewRoot 'tools\LocalPreviewInteractionTests\LocalPreviewInteractionTests.csproj') -c Release -- --sqpack $SqpackPath
} else {
    dotnet run --project (Join-Path $previewRoot 'tools\LocalPreviewInteractionTests\LocalPreviewInteractionTests.csproj') -c Release
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build (Join-Path $previewRoot 'src\FireGaze.LocalPreview\FireGaze.LocalPreview.csproj') -c Release --no-incremental
exit $LASTEXITCODE
