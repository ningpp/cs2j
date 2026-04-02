using CSharpToJava.Core.Java.Rewriters;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Project-level post-emit pass that collects Java platform module dependencies from the
/// generated code's import statements.  Each <see cref="ConversionResult"/> is annotated with
/// the set of Java module names required (e.g. "java.base", "java.sql").
///
/// <para>When <c>JavaMetadataPath</c> is not configured, this pass is a no-op.</para>
/// </summary>
public sealed class ProjectJavaModuleDependencyPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectJavaModuleDependencyPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        var javaLibrary = state.Context.TypeMappings.JavaLibrary;
        if (javaLibrary is null)
            return;

        // Aggregate module dependencies across all results
        var allModules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in state.Results)
        {
            if (string.IsNullOrWhiteSpace(result.GeneratedCode) || !result.Success)
                continue;

            var modules = CollectModuleDependencies(result, javaLibrary);
            if (modules.Count > 0)
            {
                result.JavaModuleDependencies = modules;
                foreach (var module in modules)
                    allModules.Add(module);
            }
        }
    }

    /// <summary>
    /// Parses import statements from generated Java code and queries the Java metadata
    /// to determine which modules they belong to.  Uses the preserved IR import list
    /// when available; falls back to string parsing for compatibility-generated files.
    /// </summary>
    private static HashSet<string> CollectModuleDependencies(
        ConversionResult result,
        TypeMapping.JavaModel.JavaLibraryIndex javaLibrary)
    {
        var modules = new HashSet<string>(StringComparer.Ordinal);

        // Prefer IR path: read from JavaCompilationUnit.Imports
        if (result.Compilation is not null)
        {
            foreach (var import in result.Compilation.Imports)
            {
                if (import.IsWildcard) continue; // Skip wildcard imports

                string typeName;
                if (import.IsStatic)
                {
                    // Static import: strip member name to get type name
                    var lastDot = import.Name.LastIndexOf('.');
                    typeName = lastDot > 0 ? import.Name[..lastDot] : import.Name;
                }
                else
                {
                    typeName = import.Name;
                }

                var module = javaLibrary.GetModuleFor(typeName);
                if (module is not null)
                    modules.Add(module);
            }

            return modules;
        }

        // Fallback: parse import statements from generated code string
        foreach (var line in result.GeneratedCode.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("import ", StringComparison.Ordinal))
                continue;

            if (trimmed.StartsWith("import static ", StringComparison.Ordinal))
            {
                // Static import: "import static java.lang.Math.abs;"
                var importName = trimmed.Substring("import static ".Length).TrimEnd(';', ' ');
                var lastDot = importName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var typeName = importName[..lastDot];
                    var module = javaLibrary.GetModuleFor(typeName);
                    if (module is not null)
                        modules.Add(module);
                }
            }
            else
            {
                // Regular import: "import java.util.ArrayList;"
                var importName = trimmed.Substring("import ".Length).TrimEnd(';', ' ');
                if (importName.EndsWith(".*"))
                    continue; // Skip wildcard imports

                var module = javaLibrary.GetModuleFor(importName);
                if (module is not null)
                    modules.Add(module);
            }
        }

        return modules;
    }
}

/// <summary>
/// Single-file post-emit pass that collects Java platform module dependencies from the
/// generated code.  Sets <see cref="ConversionResult.JavaModuleDependencies"/>.
/// </summary>
public sealed class SingleFileJavaModuleDependencyPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFileJavaModuleDependencyPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(SingleFilePassState state)
    {
        // Module dependencies for single-file mode are tracked during the emit pass result
        // construction in ConversionPipeline.  This pass is a placeholder for future use.
    }
}
