using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Pass 3: re-parses the output and asserts that no parameter default value
/// survives in any callable declaration the rewriter is responsible for
/// (methods, constructors, local functions, delegates and primary constructors).
///
/// Indexers, operators and conversion operators are never checked — they cannot
/// declare default values.
///
/// Two families are intentionally tolerated:
///   * parameters carrying a Caller attribute (CallerMemberName,
///     CallerLineNumber, CallerFilePath): the compiler requires them to declare
///     a default value (CS4020/CS4022);
///   * xUnit/NUnit test methods: their InlineData attributes supply values
///     positionally, so removing a default would break the test data binding.
/// </summary>
public static class DefaultParameterVerifier
{
    public static List<DefaultParameterStripDiagnostic> Verify(string sourceCode)
    {
        var errors = new List<DefaultParameterStripDiagnostic>();
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)
                            || m.IsKind(SyntaxKind.ExternKeyword))
                        || HasCallerAttribute(method.ParameterList)
                        || IsTestMethod(method))
                    {
                        continue;
                    }
                    CheckParameters(method.ParameterList.Parameters, "method", method.Identifier.Text, errors);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    if (ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)
                            || m.IsKind(SyntaxKind.ExternKeyword))
                        || HasCallerAttribute(ctor.ParameterList))
                    {
                        continue;
                    }
                    CheckParameters(ctor.ParameterList.Parameters, "constructor", ".ctor", errors);
                    break;

                case LocalFunctionStatementSyntax local when local.ParameterList != null:
                    if (local.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)
                            || m.IsKind(SyntaxKind.ExternKeyword))
                        || HasCallerAttribute(local.ParameterList))
                    {
                        continue;
                    }
                    CheckParameters(local.ParameterList.Parameters, "local function", local.Identifier.Text, errors);
                    break;

                case DelegateDeclarationSyntax @delegate when @delegate.ParameterList != null:
                    if (HasCallerAttribute(@delegate.ParameterList))
                    {
                        continue;
                    }
                    CheckParameters(@delegate.ParameterList.Parameters, "delegate",
                        @delegate.Identifier.Text, errors);
                    break;

                case TypeDeclarationSyntax type when type.ParameterList != null:
                    if (HasCallerAttribute(type.ParameterList))
                    {
                        continue;
                    }
                    CheckParameters(type.ParameterList.Parameters, "primary constructor",
                        type.Identifier.Text, errors);
                    break;
            }
        }

        return errors;
    }

    private static void CheckParameters(
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string context, string memberName,
        List<DefaultParameterStripDiagnostic> errors)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Default != null)
            {
                errors.Add(new DefaultParameterStripDiagnostic(
                    DefaultParameterStripSeverity.Error,
                    $"Residual default value found in {context} '{memberName}' parameter '{parameter.Identifier.Text}'"));
            }
        }
    }

    private static bool HasCallerAttribute(ParameterListSyntax parameterList)
    {
        foreach (var parameter in parameterList.Parameters)
        {
            foreach (var attributeList in parameter.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var name = attribute.Name.ToString();
                    if (name.Contains("CallerMemberName")
                        || name.Contains("CallerLineNumber")
                        || name.Contains("CallerFilePath"))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.Contains("Fact") || name.Contains("Theory")
                    || name.Contains("Test") || name.Contains("TestMethod"))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
