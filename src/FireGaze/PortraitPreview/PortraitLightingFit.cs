namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitLightRgb(double R, double G, double B)
{
    public bool IsValid => Valid(R) && Valid(G) && Valid(B);
    public double Channel(int channel) => channel switch { 0 => R, 1 => G, 2 => B, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and <= 255;
}

internal readonly record struct PortraitLightRgba(double R, double G, double B, int A)
{
    public PortraitLightRgb Rgb => new(R, G, B);
}

/// <summary>Display-encoded, independent monotone RGB goals. These curves do not apply a pixel filter.</summary>
internal sealed class PortraitColorToneTarget
{
    private readonly PortraitToneTarget[] channels;
    private PortraitColorToneTarget(PortraitToneTarget red, PortraitToneTarget green, PortraitToneTarget blue) => channels = [red, green, blue];

    public static bool TryCreate(IReadOnlyList<int> red, IReadOnlyList<int> green, IReadOnlyList<int> blue,
        out PortraitColorToneTarget? target)
    {
        target = null;
        if (!TryChannel(red, out var r) || !TryChannel(green, out var g) || !TryChannel(blue, out var b)) return false;
        target = new(r!, g!, b!);
        return true;
    }

    public bool TryMap(IReadOnlyList<PortraitLightRgb> source, out PortraitLightRgb[]? mapped)
    {
        mapped = null;
        if (source is null || source.Count is < 1 or > PortraitLightingFit.MaximumPixels || source.Any(pixel => !pixel.IsValid)) return false;
        mapped = new PortraitLightRgb[source.Count];
        for (var i = 0; i < source.Count; i++)
            mapped[i] = new(channels[0].Map(Round(source[i].R)), channels[1].Map(Round(source[i].G)), channels[2].Map(Round(source[i].B)));
        return true;
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static bool TryChannel(IReadOnlyList<int> values, out PortraitToneTarget? target)
    {
        target = null;
        return values is { Count: 3 } && PortraitToneTarget.TryCreate(values[0], values[1], values[2], out target);
    }
}

/// <summary>
/// Caller supplies a subject mask that excludes background, border and decoration. Sampling is stable
/// across every calibration/candidate frame; transparency changes invalidate the frame rather than shift indices.
/// Only fully opaque, straight-alpha pixels are eligible. Premultiplied captures must first be decoded by the caller.
/// </summary>
internal sealed class PortraitLightPixelSelection
{
    private readonly int totalPixels;
    private readonly int[] indices;
    private PortraitLightPixelSelection(int totalPixels, int[] indices)
    {
        this.totalPixels = totalPixels;
        this.indices = indices;
        Indices = Array.AsReadOnly(indices);
    }

    public IReadOnlyList<int> Indices { get; }

    public static bool TryCreate(IReadOnlyList<PortraitLightRgba> baseline, IReadOnlyList<bool> subjectMask,
        int maximumSamples, out PortraitLightPixelSelection? selection, out PortraitLightingFitFailure failure)
    {
        selection = null;
        failure = PortraitLightingFitFailure.InvalidInput;
        if (baseline is null || subjectMask is null || baseline.Count != subjectMask.Count || maximumSamples is < 1 or > PortraitLightingFit.MaximumPixels)
            return false;
        var eligible = new List<int>();
        for (var i = 0; i < baseline.Count; i++)
        {
            if (!subjectMask[i]) continue;
            if (baseline[i].A is < 0 or > 255) return false;
            if (baseline[i].A != 255) continue;
            if (!baseline[i].Rgb.IsValid) return false;
            eligible.Add(i);
        }
        if (eligible.Count == 0) { failure = PortraitLightingFitFailure.NoPixels; return false; }
        var count = Math.Min(eligible.Count, maximumSamples);
        var chosen = new int[count];
        for (var i = 0; i < count; i++) chosen[i] = eligible[(int)(((2L * i + 1) * eligible.Count) / (2L * count))];
        selection = new(baseline.Count, chosen);
        failure = PortraitLightingFitFailure.None;
        return true;
    }

    public bool TryExtract(IReadOnlyList<PortraitLightRgba> frame, out PortraitLightRgb[]? extracted)
    {
        extracted = null;
        if (frame is null || frame.Count != totalPixels) return false;
        var result = new PortraitLightRgb[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var pixel = frame[indices[i]];
            if (pixel.A != 255 || !pixel.Rgb.IsValid) return false;
            result[i] = pixel.Rgb;
        }
        extracted = result;
        return true;
    }
}

internal readonly record struct PortraitNativeLightRange(int Minimum, int Maximum, int Quantum)
{
    public bool IsValid => Minimum >= 0 && Maximum <= 255 && Maximum > Minimum && Quantum > 0 && (Maximum - Minimum) % Quantum == 0;
    public bool Contains(int value) => IsValid && value >= Minimum && value <= Maximum && (value - Minimum) % Quantum == 0;
}

/// <summary>All six actual controls, ordered main RGB then ambient RGB; direction and brightness remain fixed.</summary>
internal sealed record PortraitLightSample(IReadOnlyList<int> Parameters, IReadOnlyList<PortraitLightRgb> Pixels);
internal sealed record PortraitLightFitInput(PortraitLightSample Baseline, IReadOnlyList<PortraitNativeLightRange> Ranges,
    IReadOnlyList<PortraitLightSample> AxisProbes, IReadOnlyList<PortraitLightRgb> TargetPixels);

internal sealed record PortraitLightFitOptions(
    double Regularization = 0.0001,
    double MinimumProbeResponseRms = 0.05,
    double MinimumRelativeEigenvalue = 0.00001,
    double MaximumProbeExtrapolation = 3,
    double MinimumAbsoluteImprovement = 0.25,
    double MinimumRelativeImprovement = 0.02);

internal enum PortraitLightingFitFailure
{
    None,
    InvalidInput,
    NoPixels,
    NoTargetChange,
    InsufficientResponse,
    DegenerateResponse,
    PredictedImprovementInsufficient,
    ActualImprovementInsufficient,
}

internal sealed record PortraitLightingFitVerification(bool Accepted, PortraitLightingFitFailure Failure,
    double BaselineRootMeanSquareError, double RootMeanSquareError, double RelativeImprovement);

/// <summary>
/// A proposal is deliberately not acceptance. The caller must apply the legal candidate in its owned
/// sampling transaction and validate a fresh real render before committing one edit/history operation.
/// All errors use encoded RGB RMS, not a perceptual color difference or a promise to attain the curve.
/// </summary>
internal sealed class PortraitLightFitProposal
{
    private readonly PortraitLightRgb[] target;
    private readonly PortraitLightFitOptions options;

    internal PortraitLightFitProposal(int[] baseline, int[] candidate, PortraitLightRgb[] target, double baselineError,
        double predictedError, PortraitLightFitOptions options)
    {
        BaselineParameters = Array.AsReadOnly(baseline.ToArray());
        CandidateParameters = Array.AsReadOnly(candidate.ToArray());
        this.target = target.ToArray();
        this.options = options;
        BaselineRootMeanSquareError = baselineError;
        PredictedRootMeanSquareError = predictedError;
    }

    public IReadOnlyList<int> BaselineParameters { get; }
    public IReadOnlyList<int> CandidateParameters { get; }
    public double BaselineRootMeanSquareError { get; }
    public double PredictedRootMeanSquareError { get; }

    public PortraitLightingFitVerification ValidateRenderedCandidate(PortraitLightSample rendered)
    {
        if (rendered?.Parameters is null || rendered.Pixels is null || !rendered.Parameters.SequenceEqual(CandidateParameters)
            || rendered.Pixels.Count != target.Length || rendered.Pixels.Any(pixel => !pixel.IsValid))
            return new(false, PortraitLightingFitFailure.InvalidInput, BaselineRootMeanSquareError, double.NaN, 0);
        var error = PortraitLightingFit.Error(rendered.Pixels, target);
        var improvement = (BaselineRootMeanSquareError - error) / BaselineRootMeanSquareError;
        var accepted = PortraitLightingFit.Improves(BaselineRootMeanSquareError, error, options);
        return new(accepted, accepted ? PortraitLightingFitFailure.None : PortraitLightingFitFailure.ActualImprovementInsufficient,
            BaselineRootMeanSquareError, error, improvement);
    }
}

/// <summary>
/// Pure managed local finite-difference fit. Six measured axis probes build a Jacobian, normalized by
/// native range; a bounded ridge least-squares solve is quantized and locally refined on the native grid.
/// Weak or dependent responses are rejected. No native access, scheduling, rendering or persistence occurs here.
/// </summary>
internal static class PortraitLightingFit
{
    public const int MaximumPixels = 4096;
    private const int Controls = 6;

    public static bool TryPropose(PortraitLightFitInput input, PortraitLightFitOptions options,
        out PortraitLightFitProposal? proposal, out PortraitLightingFitFailure failure)
    {
        proposal = null;
        failure = PortraitLightingFitFailure.InvalidInput;
        if (!Valid(input, options)) return false;
        var baseline = input.Baseline;
        var rows = baseline.Pixels.Count * 3;
        var baselineError = Error(baseline.Pixels, input.TargetPixels);
        if (baselineError <= 1e-9) { failure = PortraitLightingFitFailure.NoTargetChange; return false; }
        var derivative = new double[rows, Controls];
        var actualProbeDelta = new int[Controls];
        var observedAxes = new bool[Controls];
        foreach (var probe in input.AxisProbes)
        {
            var axis = -1;
            for (var i = 0; i < Controls; i++)
            {
                if (!input.Ranges[i].Contains(probe.Parameters[i])) return false;
                if (probe.Parameters[i] == baseline.Parameters[i]) continue;
                if (axis != -1) return false;
                axis = i;
            }
            if (axis < 0 || observedAxes[axis]) return false;
            observedAxes[axis] = true;
            var delta = probe.Parameters[axis] - baseline.Parameters[axis];
            actualProbeDelta[axis] = delta;
            var normalizedDelta = (double)delta / (input.Ranges[axis].Maximum - input.Ranges[axis].Minimum);
            var responseSquared = 0d;
            for (var pixel = 0; pixel < baseline.Pixels.Count; pixel++)
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = probe.Pixels[pixel].Channel(channel) - baseline.Pixels[pixel].Channel(channel);
                    responseSquared += difference * difference;
                    derivative[pixel * 3 + channel, axis] = difference / normalizedDelta;
                }
            if (Math.Sqrt(responseSquared / rows) < options.MinimumProbeResponseRms)
            { failure = PortraitLightingFitFailure.InsufficientResponse; return false; }
        }
        var gram = new double[Controls, Controls];
        var gradient = new double[Controls];
        for (var row = 0; row < rows; row++)
        {
            var wanted = input.TargetPixels[row / 3].Channel(row % 3) - baseline.Pixels[row / 3].Channel(row % 3);
            for (var j = 0; j < Controls; j++)
            {
                gradient[j] += derivative[row, j] * wanted / rows;
                for (var k = 0; k < Controls; k++) gram[j, k] += derivative[row, j] * derivative[row, k] / rows;
            }
        }
        if (!Independent(gram, options.MinimumRelativeEigenvalue))
        { failure = PortraitLightingFitFailure.DegenerateResponse; return false; }
        var lower = new int[Controls]; var upper = new int[Controls];
        for (var i = 0; i < Controls; i++)
        {
            var range = input.Ranges[i];
            var trust = Math.Abs(actualProbeDelta[i]) * options.MaximumProbeExtrapolation;
            lower[i] = range.Minimum + (int)Math.Ceiling((Math.Max(range.Minimum, baseline.Parameters[i] - trust) - range.Minimum) / range.Quantum) * range.Quantum;
            upper[i] = range.Minimum + (int)Math.Floor((Math.Min(range.Maximum, baseline.Parameters[i] + trust) - range.Minimum) / range.Quantum) * range.Quantum;
            gram[i, i] += options.Regularization;
        }
        var deltaSolution = Solve(gram, gradient, baseline.Parameters, input.Ranges, lower, upper);
        var candidate = new int[Controls];
        for (var i = 0; i < Controls; i++)
        {
            var range = input.Ranges[i];
            var native = baseline.Parameters[i] + deltaSolution[i] * (range.Maximum - range.Minimum);
            candidate[i] = Math.Clamp(range.Minimum + (int)Math.Round((native - range.Minimum) / range.Quantum,
                MidpointRounding.AwayFromZero) * range.Quantum, lower[i], upper[i]);
        }
        RefineQuantized(candidate, baseline, input.TargetPixels, input.Ranges, derivative, lower, upper, options.Regularization);
        var predicted = PredictedError(candidate, baseline, input.TargetPixels, input.Ranges, derivative);
        if (!Improves(baselineError, predicted, options))
        { failure = PortraitLightingFitFailure.PredictedImprovementInsufficient; return false; }
        proposal = new(baseline.Parameters.ToArray(), candidate, input.TargetPixels.ToArray(), baselineError, predicted, options);
        failure = PortraitLightingFitFailure.None;
        return true;
    }

    internal static bool Improves(double baseline, double candidate, PortraitLightFitOptions options) =>
        double.IsFinite(candidate) && baseline - candidate > 1e-9
        && baseline - candidate + 1e-9 >= options.MinimumAbsoluteImprovement
        && (baseline - candidate) / baseline + 1e-9 >= options.MinimumRelativeImprovement;

    internal static double Error(IReadOnlyList<PortraitLightRgb> actual, IReadOnlyList<PortraitLightRgb> target)
    {
        var sum = 0d;
        for (var i = 0; i < actual.Count; i++)
            for (var channel = 0; channel < 3; channel++)
            { var delta = actual[i].Channel(channel) - target[i].Channel(channel); sum += delta * delta; }
        return Math.Sqrt(sum / (actual.Count * 3));
    }

    private static bool Valid(PortraitLightFitInput input, PortraitLightFitOptions options)
    {
        if (input?.Baseline?.Parameters is null || input.Baseline.Pixels is null || input.TargetPixels is null
            || input.Ranges is null || input.AxisProbes is null || options is null) return false;
        if (input.Baseline.Parameters.Count != Controls || input.Ranges.Count != Controls || input.AxisProbes.Count != Controls
            || input.Baseline.Pixels.Count is < 1 or > MaximumPixels || input.TargetPixels.Count != input.Baseline.Pixels.Count) return false;
        for (var i = 0; i < Controls; i++) if (!input.Ranges[i].Contains(input.Baseline.Parameters[i])) return false;
        if (input.Baseline.Pixels.Any(pixel => !pixel.IsValid) || input.TargetPixels.Any(pixel => !pixel.IsValid)) return false;
        if (input.AxisProbes.Any(probe => probe?.Parameters is null || probe.Pixels is null
            || probe.Parameters.Count != Controls || probe.Pixels.Count != input.Baseline.Pixels.Count || probe.Pixels.Any(pixel => !pixel.IsValid))) return false;
        return double.IsFinite(options.Regularization) && options.Regularization is >= 0 and <= 1000000
            && double.IsFinite(options.MinimumProbeResponseRms) && options.MinimumProbeResponseRms is > 0 and <= 255
            && double.IsFinite(options.MinimumRelativeEigenvalue) && options.MinimumRelativeEigenvalue is > 0 and < 1
            && double.IsFinite(options.MaximumProbeExtrapolation) && options.MaximumProbeExtrapolation is >= 1 and <= 8
            && double.IsFinite(options.MinimumAbsoluteImprovement) && options.MinimumAbsoluteImprovement is >= 0 and <= 255
            && double.IsFinite(options.MinimumRelativeImprovement) && options.MinimumRelativeImprovement is >= 0 and < 1;
    }

    private static bool Independent(double[,] gram, double minimumRelativeEigenvalue)
    {
        var normalized = new double[Controls, Controls];
        for (var i = 0; i < Controls; i++)
        {
            if (gram[i, i] <= 1e-18) return false;
            for (var j = 0; j < Controls; j++) normalized[i, j] = gram[i, j] / Math.Sqrt(gram[i, i] * gram[j, j]);
        }
        // Jacobi rotations for a six-by-six symmetric correlation matrix; no external numeric dependency.
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var p = 0; var q = 1; var largest = 0d;
            for (var i = 0; i < Controls; i++) for (var j = i + 1; j < Controls; j++)
                if (Math.Abs(normalized[i, j]) > largest) { largest = Math.Abs(normalized[i, j]); p = i; q = j; }
            if (largest < 1e-12) break;
            var angle = .5 * Math.Atan2(2 * normalized[p, q], normalized[q, q] - normalized[p, p]);
            var c = Math.Cos(angle); var s = Math.Sin(angle);
            var pp = normalized[p, p]; var qq = normalized[q, q]; var pq = normalized[p, q];
            normalized[p, p] = c * c * pp - 2 * s * c * pq + s * s * qq;
            normalized[q, q] = s * s * pp + 2 * s * c * pq + c * c * qq;
            normalized[p, q] = normalized[q, p] = 0;
            for (var i = 0; i < Controls; i++)
            {
                if (i == p || i == q) continue;
                var ip = normalized[i, p]; var iq = normalized[i, q];
                normalized[i, p] = normalized[p, i] = c * ip - s * iq;
                normalized[i, q] = normalized[q, i] = s * ip + c * iq;
            }
        }
        var smallest = double.PositiveInfinity; var largestEigenvalue = 0d;
        for (var i = 0; i < Controls; i++) { smallest = Math.Min(smallest, normalized[i, i]); largestEigenvalue = Math.Max(largestEigenvalue, normalized[i, i]); }
        return double.IsFinite(smallest) && smallest > largestEigenvalue * minimumRelativeEigenvalue;
    }

    private static double[] Solve(double[,] hessian, double[] gradient, IReadOnlyList<int> baseline,
        IReadOnlyList<PortraitNativeLightRange> ranges, int[] lower, int[] upper)
    {
        var x = new double[Controls];
        for (var iteration = 0; iteration < 512; iteration++)
        {
            var largestChange = 0d;
            for (var axis = 0; axis < Controls; axis++)
            {
                var rhs = gradient[axis];
                for (var other = 0; other < Controls; other++) if (other != axis) rhs -= hessian[axis, other] * x[other];
                var span = (double)(ranges[axis].Maximum - ranges[axis].Minimum);
                var next = Math.Clamp(rhs / hessian[axis, axis], (lower[axis] - baseline[axis]) / span, (upper[axis] - baseline[axis]) / span);
                largestChange = Math.Max(largestChange, Math.Abs(next - x[axis]));
                x[axis] = next;
            }
            if (largestChange < 1e-10) break;
        }
        return x;
    }

    private static void RefineQuantized(int[] candidate, PortraitLightSample baseline, IReadOnlyList<PortraitLightRgb> target,
        IReadOnlyList<PortraitNativeLightRange> ranges, double[,] derivative, int[] lower, int[] upper, double regularization)
    {
        var best = PredictedLoss(candidate, baseline, target, ranges, derivative, regularization);
        for (var sweep = 0; sweep < 32; sweep++)
        {
            var improved = false;
            for (var axis = 0; axis < Controls; axis++)
            {
                var original = candidate[axis]; var bestValue = original;
                foreach (var sign in new[] { -1, 1 })
                {
                    var trial = original + sign * ranges[axis].Quantum;
                    if (trial < lower[axis] || trial > upper[axis]) continue;
                    candidate[axis] = trial;
                    var loss = PredictedLoss(candidate, baseline, target, ranges, derivative, regularization);
                    if (loss + 1e-10 < best) { best = loss; bestValue = trial; improved = true; }
                }
                candidate[axis] = bestValue;
            }
            if (!improved) break;
        }
    }

    private static double PredictedLoss(IReadOnlyList<int> candidate, PortraitLightSample baseline,
        IReadOnlyList<PortraitLightRgb> target, IReadOnlyList<PortraitNativeLightRange> ranges, double[,] derivative, double regularization)
    {
        var error = PredictedError(candidate, baseline, target, ranges, derivative);
        var movementSquared = 0d;
        for (var axis = 0; axis < Controls; axis++)
        {
            var normalized = (double)(candidate[axis] - baseline.Parameters[axis]) / (ranges[axis].Maximum - ranges[axis].Minimum);
            movementSquared += normalized * normalized;
        }
        return error * error + regularization * movementSquared;
    }

    private static double PredictedError(IReadOnlyList<int> candidate, PortraitLightSample baseline,
        IReadOnlyList<PortraitLightRgb> target, IReadOnlyList<PortraitNativeLightRange> ranges, double[,] derivative)
    {
        var sum = 0d;
        for (var pixel = 0; pixel < baseline.Pixels.Count; pixel++) for (var channel = 0; channel < 3; channel++)
        {
            var predicted = baseline.Pixels[pixel].Channel(channel);
            for (var axis = 0; axis < Controls; axis++) predicted += derivative[pixel * 3 + channel, axis]
                * (candidate[axis] - baseline.Parameters[axis]) / (ranges[axis].Maximum - ranges[axis].Minimum);
            // Clipping is nonlinear and must be tested by the real candidate render; do not predict fabricated clipping gains.
            var error = predicted - target[pixel].Channel(channel);
            sum += error * error;
        }
        return Math.Sqrt(sum / (baseline.Pixels.Count * 3));
    }
}
