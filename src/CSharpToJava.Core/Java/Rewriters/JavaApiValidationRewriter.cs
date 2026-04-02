using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit Java IR rewriter that validates generated Java code against Java standard-library
/// metadata.  Traverses all <see cref="JavaNewExpression"/>, <see cref="JavaMethodCallExpression"/>,
/// and import references to check that the target types, methods, and constructors exist in the
/// loaded Java metadata.
///
/// <para>When metadata is not loaded (no <c>JavaMetadataPath</c> configured), this rewriter is
/// a no-op and all validation is skipped.</para>
///
/// <para>Diagnostics emitted:
/// <list type="bullet">
///   <item><c>CS2J4001</c> — target Java type not found in metadata</item>
///   <item><c>CS2J4002</c> — target Java method not found in metadata</item>
///   <item><c>CS2J4003</c> — target Java constructor not found in metadata</item>
/// </list>
/// </para>
/// </summary>
public sealed class JavaApiValidationRewriter : JavaSyntaxRewriter
{
    private readonly JavaLibraryIndex? _javaLibrary;
    private readonly DiagnosticCollector _diagnostics;
    private int _diagnosticCount;

    /// <summary>Number of diagnostics emitted during the last <c>VisitCompilationUnit</c> call.</summary>
    public int DiagnosticCount => _diagnosticCount;

    public JavaApiValidationRewriter(JavaLibraryIndex? javaLibrary, DiagnosticCollector diagnostics)
    {
        _javaLibrary = javaLibrary;
        _diagnostics = diagnostics;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _diagnosticCount = 0;

        if (_javaLibrary is null)
            return node; // No metadata loaded — skip validation

        // Validate imports
        foreach (var import in node.Imports)
        {
            if (import.IsWildcard || import.IsStatic)
                continue;

            ValidateType(import.Name);
        }

        return base.VisitCompilationUnit(node);
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        return base.VisitClassDeclaration(node);
    }

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        if (_javaLibrary is not null)
        {
            var typeName = StripGenerics(node.Type);
            var canonicalName = ResolveCanonical(typeName);

            if (canonicalName is not null)
            {
                // Validate the type exists
                ValidateType(canonicalName);

                // Validate a matching constructor exists (by argument count)
                if (node.ArrayInitializer is null)
                {
                    var constructors = _javaLibrary.FindConstructors(canonicalName);
                    if (constructors.Count > 0)
                    {
                        bool hasMatch = false;
                        foreach (var ctor in constructors)
                        {
                            if (ctor.Parameters.Count == node.Arguments.Count || ctor.VarArgs)
                            {
                                hasMatch = true;
                                break;
                            }
                        }

                        if (!hasMatch)
                        {
                            _diagnostics.Warning(
                                $"No Java constructor found for '{canonicalName}' with {node.Arguments.Count} argument(s)",
                                location: null,
                                code: "CS2J4003",
                                category: "JavaApiValidation");
                            _diagnosticCount++;
                        }
                    }
                }
            }
        }

        return base.VisitNewExpression(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        if (_javaLibrary is not null && node.Target is not null)
        {
            // Try to determine the target type from the expression
            var targetTypeName = InferTargetType(node.Target);
            if (targetTypeName is not null)
            {
                var canonicalName = ResolveCanonical(targetTypeName);
                if (canonicalName is not null)
                {
                    var methods = _javaLibrary.FindMethods(canonicalName, node.MethodName);
                    if (methods.Count == 0)
                    {
                        // Also check supertypes for inherited methods
                        bool foundInSupertype = false;
                        foreach (var supertype in _javaLibrary.GetSupertypes(canonicalName))
                        {
                            if (_javaLibrary.FindMethods(supertype, node.MethodName).Count > 0)
                            {
                                foundInSupertype = true;
                                break;
                            }
                        }

                        if (!foundInSupertype)
                        {
                            _diagnostics.Warning(
                                $"Mapped Java method '{canonicalName}.{node.MethodName}' not found in Java standard-library metadata",
                                location: null,
                                code: "CS2J4002",
                                category: "JavaApiValidation");
                            _diagnosticCount++;
                        }
                    }
                }
            }
        }

        return base.VisitMethodCallExpression(node);
    }

    // ─── Private helpers ────────────────────────────────────

    private void ValidateType(string canonicalName)
    {
        if (IsPrimitiveOrCommon(canonicalName))
            return;

        if (_javaLibrary!.FindType(canonicalName) is null)
        {
            _diagnostics.Warning(
                $"Mapped Java type '{canonicalName}' not found in Java standard-library metadata",
                location: null,
                code: "CS2J4001",
                category: "JavaApiValidation");
            _diagnosticCount++;
        }
    }

    /// <summary>
    /// Attempts to infer a Java type name from the target expression of a method call.
    /// Returns <see langword="null"/> when the type cannot be determined statically.
    /// </summary>
    private static string? InferTargetType(JavaExpression target)
    {
        return target switch
        {
            // new ArrayList<String>().add(x) — target is JavaNewExpression
            JavaNewExpression newExpr => StripGenerics(newExpr.Type),
            // ClassName.staticMethod() — target is identifier that looks like a type name
            JavaIdentifierExpression id when id.Name.Length > 0 && char.IsUpper(id.Name[0]) => id.Name,
            _ => null,
        };
    }

    /// <summary>
    /// Strips generic type parameters: "ArrayList&lt;String&gt;" → "ArrayList".
    /// Also strips array brackets: "int[]" → "int".
    /// </summary>
    internal static string StripGenerics(string typeName)
    {
        var angleIdx = typeName.IndexOf('<');
        if (angleIdx > 0)
            return typeName.Substring(0, angleIdx);

        // Strip array brackets
        var bracketIdx = typeName.IndexOf('[');
        if (bracketIdx > 0)
            return typeName.Substring(0, bracketIdx);

        return typeName;
    }

    /// <summary>
    /// Resolves a simple Java type name to its canonical form for <see cref="JavaLibraryIndex"/> lookups.
    /// Returns <see langword="null"/> for project-local types that can't be resolved.
    /// </summary>
    internal string? ResolveCanonical(string typeName)
    {
        if (_javaLibrary is null)
            return null;

        // Already canonical (contains dot)
        if (typeName.Contains('.'))
            return typeName;

        // Try well-known java.lang types
        if (_javaLibrary.FindType("java.lang." + typeName) is not null)
            return "java.lang." + typeName;

        // Try java.util types (very common)
        if (_javaLibrary.FindType("java.util." + typeName) is not null)
            return "java.util." + typeName;

        // Try java.io types
        if (_javaLibrary.FindType("java.io." + typeName) is not null)
            return "java.io." + typeName;

        return null; // Unknown — skip validation for project-local types
    }

    private static bool IsPrimitiveOrCommon(string javaTypeName)
    {
        return javaTypeName is "int" or "long" or "short" or "byte" or "float" or "double"
            or "boolean" or "char" or "void"
            or "Object" or "String" or "var"
            or "Integer" or "Long" or "Short" or "Byte" or "Float" or "Double" or "Boolean" or "Character";
    }
}
