namespace CSharpToJava.Tests;

/// <summary>
/// Serializes every test class that mutates the process-wide
/// <see cref="CSharpToJava.CLI.MsBuildCompileEvaluator.EvaluationOverride"/> /
/// <see cref="CSharpToJava.CLI.MsBuildCompileEvaluator.PackageReferenceEvaluationOverride"/>
/// injection seams. The seams are static, so classes in different xUnit
/// collections would otherwise race and observe each other's overrides.
/// </summary>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class MsBuildCompileEvaluatorOverridesCollection
{
    public const string CollectionName = "MSBuildCompileEvaluatorOverrides";
}
