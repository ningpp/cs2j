namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed record DefaultParameterStripperOptions(
    bool Verbose = false,
    bool Strict = false,
    bool GenerateOverloads = true,
    bool PreserveDefaultMetadata = true);
