namespace CSharpToJava.Core.DefaultParameterStripper;

public enum DefaultParameterStripSeverity { Info, Warning, Error }

public sealed record DefaultParameterStripDiagnostic(
    DefaultParameterStripSeverity Severity,
    string Message);
