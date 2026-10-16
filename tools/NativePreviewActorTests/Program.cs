using System.Reflection;

internal static class Program
{
    static int Main(string[] args)
    {
        var actorType = Assembly.GetExecutingAssembly().GetType("FireGaze.LocalPreview.Runtime.NativePreviewActor");
        if (actorType == null)
        {
            Console.WriteLine("FAIL native preview actor lifecycle: feature missing (NativePreviewActor production type is absent)");
            return 1;
        }
        if (actorType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) == null)
        {
            Console.WriteLine("FAIL native preview actor lifecycle: feature missing (Create entry point is absent)");
            return 1;
        }
        return NativeHarness.Run(args);
    }
}
