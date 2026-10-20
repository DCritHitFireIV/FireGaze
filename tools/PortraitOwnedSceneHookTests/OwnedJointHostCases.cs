using FireGaze.PortraitPreview;

internal static class OwnedJointHostCases
{
    internal static int Run()
    {
#if !HAS_OWNED_JOINT_HOST
        Console.Error.WriteLine("FAIL one physical joint owner is absent: typed mutation ingress cannot remain live with the consumer Render callback");
        return 1;
#else
        return JointPhysicalHostCases.Run();
#endif
    }
}
