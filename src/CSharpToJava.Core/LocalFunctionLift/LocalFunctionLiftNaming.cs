namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Naming utilities: camelCase conversion, member conflict resolution,
/// and capture parameter naming with _cs2j_cap_ prefix.
/// </summary>
internal static class LocalFunctionLiftNaming
{
    /// <summary>
    /// Base name = first character lowercased, preserving _ prefix and @ escape.
    /// If the result is a C# keyword, a suffix is appended to avoid conflicts.
    /// </summary>
    internal static string GetBaseName(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return identifier;
        // Preserve @ escape prefix
        if (identifier[0] == '@')
        {
            if (identifier.Length == 1)
                return identifier;
            return "@" + char.ToLowerInvariant(identifier[1]) + identifier[2..];
        }
        // Preserve _ prefix
        if (identifier[0] == '_')
        {
            if (identifier.Length == 1)
                return identifier;
            return "_" + char.ToLowerInvariant(identifier[1]) + identifier[2..];
        }
        var baseName = char.ToLowerInvariant(identifier[0]) + identifier[1..];
        // Avoid identifiers that are reserved words in C# or in the Java target.
        // Contextual keywords (add, remove, get, set, value, var, async, await, from,
        // select, where, yield, ...) are legal identifiers, so they must NOT be renamed:
        // a local function named `Add` has to lift to `add`.
        if (IsReservedIdentifier(baseName))
            baseName += "Local";
        return baseName;
    }

    /// <summary>
    /// C# reserved keywords plus Java reserved words and restricted identifiers — the
    /// union of names that cannot be used verbatim as a lifted method name. C#
    /// contextual keywords are deliberately excluded: they are valid identifiers in
    /// both languages.
    /// </summary>
    private static bool IsReservedIdentifier(string s)
    {
        return s switch
        {
            // C# reserved keywords
            "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch"
            or "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default"
            or "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit"
            or "extern" or "false" or "finally" or "fixed" or "float" or "for" or "foreach"
            or "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or "is"
            or "lock" or "long" or "namespace" or "new" or "null" or "object" or "operator"
            or "out" or "override" or "params" or "private" or "protected" or "public"
            or "readonly" or "ref" or "return" or "sbyte" or "sealed" or "short" or "sizeof"
            or "stackalloc" or "static" or "string" or "struct" or "switch" or "this" or "throw"
            or "true" or "try" or "typeof" or "uint" or "ulong" or "unchecked" or "unsafe"
            or "ushort" or "using" or "virtual" or "void" or "volatile" or "while"
            // Java reserved words that C# does not reserve
            or "assert" or "boolean" or "extends" or "final" or "implements" or "import"
            or "instanceof" or "native" or "package" or "strictfp" or "super" or "synchronized"
            or "throws" or "transient"
            // Java restricted identifiers (cannot name a member that is also a type)
            or "record" or "permits" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Resolves a unique name by appending 2, 3, ... when it conflicts
    /// with existing member names or already-assigned names.
    /// </summary>
    internal static string ResolveUniqueName(
        string baseName,
        HashSet<string> usedNames)
    {
        var name = baseName;
        var suffix = 2;
        while (usedNames.Contains(name))
        {
            name = baseName + suffix;
            suffix++;
        }
        usedNames.Add(name);
        return name;
    }

    /// <summary>
    /// Capture parameter name: _cs2j_cap_&lt;originalName&gt;.
    /// If it conflicts with an existing capture parameter, append _2, _3, ...
    /// </summary>
    internal static string GetCaptureParameterName(
        string originalName,
        HashSet<string> existingCaptureParamNames)
    {
        var capName = "_cs2j_cap_" + originalName;
        if (!existingCaptureParamNames.Contains(capName))
        {
            existingCaptureParamNames.Add(capName);
            return capName;
        }
        var suffix = 2;
        while (existingCaptureParamNames.Contains($"{capName}_{suffix}"))
        {
            suffix++;
        }
        var final = $"{capName}_{suffix}";
        existingCaptureParamNames.Add(final);
        return final;
    }
}
