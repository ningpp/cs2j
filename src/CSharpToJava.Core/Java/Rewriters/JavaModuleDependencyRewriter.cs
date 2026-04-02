using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit Java IR rewriter that collects Java module dependencies for the generated code.
/// Traverses all imports in <see cref="JavaCompilationUnit"/> and queries the
/// <see cref="JavaLibraryIndex"/> to determine which Java platform modules are required.
///
/// <para>When metadata is not loaded, this rewriter is a no-op.</para>
///
/// <para>After visiting, the <see cref="ModuleDependencies"/> property contains the set of
/// module names (e.g. "java.base", "java.sql") required by the generated code.</para>
/// </summary>
public sealed class JavaModuleDependencyRewriter : JavaSyntaxRewriter
{
    private readonly JavaLibraryIndex? _javaLibrary;
    private readonly HashSet<string> _moduleDependencies = new(StringComparer.Ordinal);

    /// <summary>
    /// After <see cref="VisitCompilationUnit"/> completes, contains the set of Java module
    /// names required by the generated code.
    /// </summary>
    public IReadOnlySet<string> ModuleDependencies => _moduleDependencies;

    public JavaModuleDependencyRewriter(JavaLibraryIndex? javaLibrary)
    {
        _javaLibrary = javaLibrary;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _moduleDependencies.Clear();

        if (_javaLibrary is null)
            return node;

        // Collect module dependencies from imports
        foreach (var import in node.Imports)
        {
            if (import.IsWildcard)
                continue;

            var typeName = import.IsStatic ? ExtractTypeFromStaticImport(import.Name) : import.Name;
            if (typeName is null)
                continue;

            var module = _javaLibrary.GetModuleFor(typeName);
            if (module is not null)
            {
                _moduleDependencies.Add(module);
            }
        }

        // No need to traverse deeper — imports capture all external type references
        return node;
    }

    /// <summary>
    /// Extracts the type name from a static import (e.g. "java.lang.Math.abs" → "java.lang.Math").
    /// </summary>
    private static string? ExtractTypeFromStaticImport(string staticImportName)
    {
        var lastDot = staticImportName.LastIndexOf('.');
        if (lastDot <= 0)
            return null;

        return staticImportName.Substring(0, lastDot);
    }
}
