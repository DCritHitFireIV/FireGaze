using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Runtime.InteropServices;
unsafe class Program
{
    static int passed;
    static void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed++;Console.WriteLine("PASS "+label);}
    static int Main()
    {
        var root=Path.Combine(Path.GetTempPath(),"firegaze-light-integration-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RaptureGearsetModule.Value=(RaptureGearsetModule*)NativeMemory.AllocZeroed((nuint)sizeof(RaptureGearsetModule));
        try {DirectionalLightCases.Run(root,Check);Console.WriteLine($"PortraitLightIntegrationTests: {passed} passed, 0 failed");return 0;}
        catch(Exception exception){Console.Error.WriteLine(exception);return 1;}
        finally {NativeMemory.Free(RaptureGearsetModule.Value);Directory.Delete(root,true);}
    }
}
