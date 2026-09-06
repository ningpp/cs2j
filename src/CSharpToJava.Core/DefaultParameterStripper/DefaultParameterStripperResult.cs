namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed record DefaultParameterStripperResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics,
    DefaultParameterStripperStatistics Statistics);
