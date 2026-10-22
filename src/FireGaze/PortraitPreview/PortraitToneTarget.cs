namespace FireGaze.PortraitPreview;

/// <summary>
/// A low-degree-of-freedom goal in display-encoded luma, not an applied image filter or native lighting fit.
/// Input positions are fixed at 0,64,128,192,255. Only three interior outputs vary; endpoints stay 0 and 255.
/// Outputs must be nondecreasing and in range. This model makes no claim that the two native light controls
/// can attain the goal; a future fit must use real rendered samples and report any remaining difference.
/// </summary>
internal sealed class PortraitToneTarget
{
    private static readonly int[] Positions = [0, 64, 128, 192, 255];
    private readonly byte[] outputs;

    private PortraitToneTarget(byte[] outputs)
    {
        this.outputs = outputs;
        ControlPoints = Array.AsReadOnly(outputs);
    }

    public IReadOnlyList<byte> ControlPoints { get; }

    public static bool TryCreate(int shadows, int middle, int highlights, out PortraitToneTarget? target)
    {
        target = null;
        if (shadows < 0 || highlights > 255 || middle < shadows || highlights < middle) return false;
        target = new([(byte)0, (byte)shadows, (byte)middle, (byte)highlights, (byte)255]);
        return true;
    }

    public byte Map(int encodedLuma)
    {
        if (encodedLuma is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(encodedLuma));
        var segment = Math.Min(encodedLuma / 64, 3);
        var left = Positions[segment]; var span = Positions[segment + 1] - left;
        var offset = encodedLuma - left;
        return (byte)(outputs[segment]
            + ((outputs[segment + 1] - outputs[segment]) * offset + span / 2) / span);
    }
}
