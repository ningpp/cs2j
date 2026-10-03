namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// M4 severity policy (design §6): the Warning-grade codes that an explicit
/// strict run escalates to Error. CS2J1206/1230/1231 are always Error;
/// CS2J1290 is a permanent Warning and never escalates.
/// </summary>
public static class ExtensionMethodResolvePolicy
{
    /// <summary>Codes that are Warning by default and Error under <c>--extension-methods-strict</c>.</summary>
    public static readonly IReadOnlySet<string> StrictEscalatedCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ExtensionMethodCodes.Ambiguous,              // CS2J1201
        ExtensionMethodCodes.Unbound,                // CS2J1202
        ExtensionMethodCodes.ExternalHostNoMapping,  // CS2J1203
        ExtensionMethodCodes.RefMismatch,            // CS2J1204
        ExtensionMethodCodes.NullConditional,        // CS2J1205
        ExtensionMethodCodes.ReducedMethodGroup,     // CS2J1207
        ExtensionMethodCodes.ExpressionTreeIdentityFailed, // CS2J1210
        ExtensionMethodCodes.ResidualFound,          // CS2J1220
    };

    public static bool IsStrictEscalated(string code) => StrictEscalatedCodes.Contains(code);

    /// <summary>Severity of a Warning-grade code under the given strict flag.</summary>
    public static ExtensionSeverity Severity(string code, bool strict)
        => strict && IsStrictEscalated(code) ? ExtensionSeverity.Error : ExtensionSeverity.Warning;

    /// <summary>CS2J1220 residual gate severity: Error in strict runs, Warning otherwise (tree stays compilable).</summary>
    public static ExtensionSeverity ResidualSeverity(bool strict)
        => strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
}
