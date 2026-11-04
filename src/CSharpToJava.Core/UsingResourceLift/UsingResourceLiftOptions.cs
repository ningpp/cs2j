namespace CSharpToJava.Core.UsingResourceLift;

/// <summary>
/// Options for the independent <c>using-resource-lift</c> control-flow pass.
/// The pass lifts a <c>using</c> resource expression that contains a
/// <c>ref</c>/<c>out</c> read-back (or a <c>ByRef&lt;T&gt;</c> factory call) into
/// a preceding ordinary local declaration, so the Java statement gate never
/// sees a resource expression with pending post statements.
/// </summary>
public sealed class UsingResourceLiftOptions
{
    /// <summary>Lift <c>using (T x = expr)</c> declarations.</summary>
    public bool LiftDeclaredResources { get; init; } = true;

    /// <summary>Lift <c>using (expr)</c> expression resources into a <c>var</c> temp.</summary>
    public bool LiftExpressionResources { get; init; } = true;
}
