unsafe class Program
{
    static int passed,failed;
    static void Check(bool condition,string label){if(condition)passed++;else failed++;Console.WriteLine((condition?"PASS ":"FAIL ")+label);}
    static int Main(){DirectionalLightNativeCases.Run(Check);LightingSpaceNativeCases.Run(Check);Console.WriteLine($"PortraitLightNativeTests: {passed} passed, {failed} failed");return failed==0?0:1;}
}
