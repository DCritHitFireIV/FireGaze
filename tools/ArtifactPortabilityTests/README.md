# ArtifactPortabilityTests

Inspects the actual Release payload (`src/FireGaze/bin/Release`), including FireGaze, its local-preview core and dnlib. Pass an alternate artifact directory as the first argument when needed.

```text
dotnet run --project tools/ArtifactPortabilityTests/ArtifactPortabilityTests.csproj -c Release
```

The 19 checks cover artifact presence, managed PE readability, CodeView/PDB paths, and literal Windows user-profile paths in UTF-8 and both UTF-16 byte alignments. Owned assemblies cannot embed absolute CodeView/PDB paths. A third-party library's public CI debug path is permitted; its actual user-profile paths are still rejected. Redaction placeholders such as `%USER%` are not actual user paths.

A Release assembly built with `DebugType=None` can legitimately contain no CodeView entries. This is a passing result. The checks do not edit DLLs, load plugins, or inspect historical reports. Translation text containing a public default localhost service URL is outside these checks.
