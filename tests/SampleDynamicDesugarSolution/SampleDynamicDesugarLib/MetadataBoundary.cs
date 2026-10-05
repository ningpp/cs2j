namespace SampleDynamicDesugarLib;

/// <summary>
/// DS019 metadata-boundary surface. The referenced SampleDynamicBoundary
/// assembly (external/SampleDynamicBoundary.dll) is written with the
/// <c>dynamic</c> keyword, so in metadata its signatures are
/// <c>object</c> + <see cref="System.Runtime.CompilerServices.DynamicAttribute"/>.
/// C# forbids writing that shape in source (CS1970, not suppressible), which is
/// why the fixture consumes it through metadata rather than declaring it.
/// Every use-site below is a registered DS019 residual (return/parameter/
/// property shapes), byte-retained under Rewrite.
/// </summary>
public static class MetadataBoundary
{
    /// <summary>Dynamic-return metadata method: DS019 invocation + DS002 cast.</summary>
    public static int EchoThroughMetadata()
        => (int)SampleDynamicBoundary.DynamicShapes.Echo(44);

    /// <summary>object+[Dynamic] metadata parameter: DS019 argument site.</summary>
    public static string FormatThroughMetadata()
        => SampleDynamicBoundary.DynamicShapes.Format(77);

    /// <summary>Dynamic metadata property: DS019 member access on get and set.</summary>
    public static int PropertyThroughMetadata()
    {
        SampleDynamicBoundary.DynamicShapes.Value = 88;
        return (int)SampleDynamicBoundary.DynamicShapes.Value;
    }
}
