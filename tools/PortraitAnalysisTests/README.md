# Portrait analysis pure-core verification

This suite links the production sources directly and calls no game, plugin, image upload, file cache or rendering API.

```powershell
dotnet run --project tools/PortraitAnalysisTests/PortraitAnalysisTests.csproj --configuration Release
```

## API contract

`PortraitImageAnalysis.Analyze(pixels, width, height, stride, order, options)` accepts positive-stride, 8-bit RGBA or BGRA pixels of up to 1,048,576 pixels. Trailing padding after the last row is optional; preceding row padding is ignored. Invalid dimensions, arithmetic ranges, truncated buffers, unknown pixel order/alpha mode and invalid thresholds are rejected before analysis.

Alpha-zero pixels never count. `MinimumAlpha` is inclusive and constrained to 1..255. Each accepted pixel contributes one histogram sample; this is not alpha-weighted compositing onto an invented background. Straight-alpha samples retain their RGB; premultiplied samples are checked against their alpha and decoded to straight RGB. The caller must name its actual representation. The core does not infer pixel order, color space, HDR, native texture format or premultiplication from image content.

`PortraitImageAnalysisResult` has `Red`, `Green`, `Blue`, `Luma`, visible/ignored counts, clipping fractions, `AttentionWidth`, `AttentionHeight`, `Attention`, and `Hotspots`. All exposed collections are read-only wrappers. Luma applies Rec.709 weights to display-encoded channel values; it is not physical luminance or RAW exposure. Inclusive black/white thresholds are explicit 0..255 values, with black < white. Luma clipping and individual channel highlights use the same visible-pixel denominator.

Attention uses local RGB contrast, visible-neighbor edges and a weak multiplicative center bias. It works on a surface bounded to 128 pixels on either side, yields at most three non-overlapping source-pixel rectangles and returns no hotspots for uniform or fully transparent scenes. A normalized score is an internal heuristic, not a percentage of viewers or probability of their first fixation. It does not model faces, text meaning, viewing tasks or actual eye movements. Do not name its output eye tracking; the UI must disclose the estimate/heuristic.

`PortraitLightDirection` offers `TryNative`, `TryNormalize`, `TryDirection`, `TryAngles`, `TryQuantize`, `TryProject` and `TryFromProjection`. Both native short fields are documented as -180..180 in [FFXIVClientStructs commit d6397a76b3a63c2b247025dc296653182b718ca6](https://github.com/aers/FFXIVClientStructs/blob/d6397a76b3a63c2b247025dc296653182b718ca6/FFXIVClientStructs/FFXIV/Client/UI/Misc/CharaViewPortrait.cs). The UI representation folds latitude to -90..90 without changing the direction. +X is right, +Y up, +Z forward; positive native vertical points down. This convention matches the inspected Photobooth sphere math, but its apparent orientation against the actual game's render still requires runtime calibration.

Top view uses `(X,-Z)` with hidden Y; side view uses `(Z,-Y)` with hidden X. Inverting a projection requires an explicit hidden-axis hemisphere and clamps drags outside the unit disk. No ambient-light position is modeled. These are illustrative direction projections, not a calibrated portrait camera or newly movable native lights.

`PortraitAnalysisRequest.TryStart(key, factory)` accepts a `Func<CancellationToken,Task<PortraitImageAnalysisResult>>`, runs it on the thread pool and allows one worker at a time. A successful matching key is cached; a failed key is retryable. The caller invalidates on any view close, scene/character change or source-state change. A cancellation-ignoring old task keeps its slot until completion; it cannot publish after invalidation. `Invalidate` and `Dispose` do not wait for the task, Present, a resource lease or cancellation callbacks.

Accepted factories always run exactly once even if canceled before dispatch, so their own `using/finally` can release a captured lease. Rejected factories never run; their caller still owns any lease. The service never owns native resources. It uses asynchronous cancellation and observes callback failures; error UI does not receive raw worker exception strings. The caller should still keep its real diagnostic logging outside the product copy.

`PortraitToneTarget.TryCreate(shadows, middle, highlights, out target)` is a goal only. Input control positions are fixed at 0/64/128/192/255; endpoints remain 0/255; three interior outputs must be nondecreasing and within range. `Map(0..255)` uses quantized monotonic piecewise-linear interpolation. It does not alter pixels, colors, materials, game lighting or native state and does not claim that native lights can attain the target.

## Evidence and limits

Machine-specific execution logs are kept in the task's external evidence directory, not in the source or runtime package. Names below identify those retained logs.

- `red-missing-core.log`: compiled executable, then three expected missing-feature failures before production implementation.
- `green-core.log`: first histogram/attention/sphere implementation, 487 passed.
- `red-missing-lifecycle.log`: existing 487 checks pass, missing lifecycle behavior fails before its implementation.
- `green-lifecycle.log`: first lifecycle core, 517 passed.
- `green-boundaries.log`: equal-luma color contrast, maximum pixel budget and accepted-before-dispose dispatch checks, 540 passed.
- `red-missing-tone-target.log`: existing 540 checks pass, missing target model fails before its implementation.
- `green-final.log` and `build-final.log`: final current-source checks and independent compile.

The suite exercises actual arrays and `TaskCompletionSource` completion order, including a cancellation callback held behind a separate completion signal. It covers invalid inputs, source immutability, color order/stride, straight/premultiplied alpha, threshold inclusivity, empty/flat scenes, hotspot separation, sphere poles/wrap/large and subnormal vectors, explicit projection hemispheres, stale results/errors, retry, disposal and accepted-factory cleanup.

This is offline math and lifecycle qualification. It does not establish an integrated native-light UI, live texture-readback correctness, whole-adventurer-plate capture, semantic saliency, human first-fixation accuracy, automatic light fitting or whole-plugin/game acceptance. No UI, Feature registration, version or build-gate source is owned or changed by this subtask. Root integration must run the current full project gate and runtime acceptance independently.
