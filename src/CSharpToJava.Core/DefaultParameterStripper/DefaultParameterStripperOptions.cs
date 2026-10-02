namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed record DefaultParameterStripperOptions(
    bool Verbose = false,
    bool Strict = false,
    bool GenerateOverloads = true,
    bool PreserveDefaultMetadata = true,
    // C48 params-collections rule set; null/disabled = zero behavior change.
    // The type is fully qualified because the "ParamsCollections" namespace
    // shares the property's name.
    CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.ParamsCollectionsOptions? ParamsCollections = null);
