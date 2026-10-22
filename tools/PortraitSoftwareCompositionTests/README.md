# Portrait software composition boundary

Run with the installed Dalamud SDK available:

```powershell
dotnet run --project tools/PortraitSoftwareCompositionTests -c Release
```

The executable links the production compositor, layer models, geometry, and session types.
Only `ITextureReadbackProvider.GetRawImageAsync` and
`ITextureProvider.CreateFromRawAsync` are replaced. The fake texture's ImGui handle
throws if accessed. These are offline ownership and pixel tests; they do not establish
game or GPU acceptance.

## Pixel contract

- Layers are applied in input order over a transparent canvas.
- Placement is normalized to the output. Clipping preserves the original UV mapping.
- Normalized UV endpoints may crop or reverse an axis. Sampling clamps to source edges.
- Bilinear interpolation operates on linear, premultiplied RGBA. It therefore cannot
  mix hidden RGB from transparent texels into visible edges.
- UNORM RGBA/BGRA and their sRGB variants are accepted (DXGI 28, 87, 29, 91).
  sRGB RGB channels are decoded before interpolation; alpha is always linear.
- ImGui packed tint uses ABGR byte order. RGB and alpha multiply the sampled layer.
- Source-over uses `C = Cs + Cd * (1 - As)` and
  `A = As + Ad * (1 - As)` with premultiplied colors.
- The final upload is straight-alpha RGBA UNORM (DXGI 28). Fully transparent output
  has zero RGB. Quantization rounds halves away from zero.

## Bounds and ownership

- Output: at most 1,048,576 pixels. Source: at most 16,777,216 pixels and 64 MiB
  of raw data. Each dimension is at most 16,384. At most three layers are retained.
- Padded pitch is accepted; the last row need not contain unused trailing padding.
- The entry method retains every source synchronously before its first asynchronous
  boundary. Callers may immediately dispose the original wrappers after it returns.
- Each layer has an independent retained wrapper, including duplicate source textures.
- Reads are sequential and request the full source without GPU resizing. Composition
  occurs on the CPU; official readback requests DXGI 28 normalization (including compressed
  game UI textures). Readback/normalization and final upload cross the public GPU boundary.
- Cancellation does not abandon a physical read or upload. Late resources remain owned
  until completion. A late uploaded output is released instead of published.
- All cleanup is attempted. A cleanup error preserves an existing operation/cancellation
  error. If cleanup alone fails, the uploaded but unpublished output is also released.
- Uploaded width and height must match the requested output; mismatches are released.

## Recorded evidence

- `red-missing-compositor.txt`: absent implementation fails.
- `green-first.txt`: first implementation passes 73 checks.
- `red-release-failure.txt`: a source-disposal error hid an uploaded resource.
- `green-release-failure.txt`: that leak and exception masking are fixed (78 checks).
- `red-output-budget.txt`: excessive layers were accepted.
- `red-upload-dimensions.txt`: wrong upload dimensions were published.
- `green-output-budget.txt`: bounded layers, output validation, and later-layer failures
  pass along with prior tests (88 checks).
- `red-normalization.txt` / `green-normalization.txt`: compressed textures request official
  RGBA normalization and consume the normalized pixels (90 checks).

Related integration verification after ownership changes: `PortraitCaptureTests`
114 passed, including capture stage diagnostics; `PortraitHistoryPhotoIntegrationTests`
41 passed. The complete offline gate ran 61 suites with no skips. No game operations
were performed by this test task.
