namespace SampleDynamicDesugarLib;

/// <summary>
/// DS011 cross-boundary signatures: a dynamic parameter and a dynamic return
/// type are never rewritten automatically in M2. The static-typed wrapper keeps
/// the dynamic surface reachable from xUnit without dynamic locals in the test
/// project.
/// </summary>
public static class DynamicSignatures
{
    public static dynamic EchoDynamic(dynamic value) => value;

    /// <summary>DS016 argument boundary, DS015 dynamic operation and the DS002
    /// user-written explicit cast all live here.</summary>
    public static int EchoAndAddTwo(int seed) => (int)(EchoDynamic(seed) + 2);
}
