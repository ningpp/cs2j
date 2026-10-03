namespace CSharpToJava.Core.VarianceAudit;

/// <summary>Per-file audit result: diagnostics only (the pass never rewrites, design D1).</summary>
public sealed class VarianceFileAudit
{
    public List<VarianceAuditDiagnostic> Diagnostics { get; } = new();
}