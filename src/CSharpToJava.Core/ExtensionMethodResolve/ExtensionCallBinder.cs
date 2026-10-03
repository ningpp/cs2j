using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Single-file analysis (EM-BIND): enumerates invocation nodes, consumes
/// <see cref="SemanticModel.GetSymbolInfo"/> and classifies the ones bound as
/// <c>MethodKind.ReducedExtension</c>; no candidate resolution is rebuilt here.
/// Also detects reduced member access that is not an invocation (EM-GROUP).
///
/// M0 shell: no rules are active, so analysis produces zero bindings and zero
/// diagnostics — every input file stays byte-identical.
/// </summary>
internal sealed class ExtensionCallBinder
{
    public List<ExtensionCallBinding> Bindings { get; } = new();
    public List<ExtensionDiagnostic> Diagnostics { get; } = new();

    public ExtensionCallBinder(SemanticModel model, ExtensionMethodResolveOptions options)
    {
        Model = model;
        Options = options;
    }

    public SemanticModel Model { get; }

    public ExtensionMethodResolveOptions Options { get; }

    /// <summary>
    /// Analyze one syntax tree. M0 selects nothing. M1 binds reduced
    /// invocations; M2 adds method-group/ref/expression-tree classification.
    /// </summary>
    public void Analyze()
    {
        // M0: rules empty — Bindings/Diagnostics stay empty on purpose.
    }
}
