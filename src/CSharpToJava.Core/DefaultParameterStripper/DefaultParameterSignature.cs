using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// The parameter shape of a declaration whose default values have been (or are
/// about to be) stripped. It is used to complete call sites that omit optional
/// arguments: for every parameter that the caller does not supply, the verbatim
/// default expression is inserted as a named argument.
/// </summary>
public sealed class DefaultParameterSignature
{
    /// <summary>Raw parameter identifier text (keeps a leading <c>@</c> when escaped).</summary>
    public string[] Names { get; }

    /// <summary>Verbatim default expression text, or <see langword="null"/> when the parameter has none.</summary>
    public string?[] DefaultTexts { get; }

    /// <summary>True for <c>params</c> parameters (they may legitimately be omitted).</summary>
    public bool[] IsParams { get; }

    public int Count => Names.Length;

    public bool HasAnyDefault { get; }

    public DefaultParameterSignature(string[] names, string?[] defaultTexts, bool[] isParams, bool hasAnyDefault)
    {
        Names = names;
        DefaultTexts = defaultTexts;
        IsParams = isParams;
        HasAnyDefault = hasAnyDefault;
    }

    public static DefaultParameterSignature Create(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var count = parameters.Count;
        var names = new string[count];
        var defaults = new string?[count];
        var isParams = new bool[count];
        var any = false;

        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[i];
            names[i] = parameter.Identifier.Text;
            defaults[i] = ResolveDefaultText(parameter);
            if (defaults[i] != null)
            {
                any = true;
            }
            isParams[i] = parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));
        }

        return new DefaultParameterSignature(names, defaults, isParams, any);
    }

    /// <summary>
    /// The value a caller must pass when it omits this parameter. Ordinary
    /// defaults use the declared expression; <c>[Optional]</c> parameters (which
    /// carry no default expression) are treated as <c>default</c>.
    /// </summary>
    private static string? ResolveDefaultText(ParameterSyntax parameter)
    {
        if (parameter.Default != null)
        {
            return parameter.Default.Value.ToString();
        }

        return HasOptionalAttribute(parameter) ? "default" : null;
    }

    private static bool HasOptionalAttribute(ParameterSyntax parameter)
    {
        foreach (var list in parameter.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                var text = attribute.Name.ToString();
                if (text.EndsWith("Optional", StringComparison.Ordinal)
                    || text.EndsWith("OptionalAttribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public int IndexOfName(string rawName)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], rawName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Drops the leading parameter. Used for reduced extension-method symbols,
    /// whose parameter list does not contain the receiver.
    /// </summary>
    public DefaultParameterSignature WithoutFirst()
    {
        if (Count <= 1)
        {
            return new DefaultParameterSignature([], [], [], false);
        }

        var names = new string[Count - 1];
        var defaults = new string?[Count - 1];
        var isParams = new bool[Count - 1];
        Array.Copy(Names, 1, names, 0, names.Length);
        Array.Copy(DefaultTexts, 1, defaults, 0, defaults.Length);
        Array.Copy(IsParams, 1, isParams, 0, isParams.Length);

        var any = false;
        foreach (var d in defaults)
        {
            if (d != null) any = true;
        }

        return new DefaultParameterSignature(names, defaults, isParams, any);
    }
}
