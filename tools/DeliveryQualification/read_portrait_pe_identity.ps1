param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$portraitFile = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
$portraitPE = [System.Reflection.PortableExecutable.PEReader]::new($portraitFile)
try {
    if (!$portraitPE.HasMetadata) { throw 'Managed PE metadata required' }
    $portraitReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($portraitPE)
    $portraitAssembly = $portraitReader.GetAssemblyDefinition()
    $portraitModule = $portraitReader.GetModuleDefinition()
    if ($portraitReader.GetString($portraitAssembly.Name) -ne 'FireGaze') { throw 'Unexpected assembly name' }
    [ordered]@{
        version = $portraitAssembly.Version.ToString()
        mvid = $portraitReader.GetGuid($portraitModule.Mvid).ToString()
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    } | ConvertTo-Json -Compress
} finally {
    $portraitPE.Dispose()
    $portraitFile.Dispose()
}
