namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Naming utilities: camelCase conversion, member conflict resolution,
/// and capture parameter naming with _cs2j_cap_ prefix.
/// </summary>
internal static class LocalFunctionLiftNaming
{
    /// <summary>
    /// Base name = first character lowercased, preserving _ prefix and @ escape.
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
        return char.ToLowerInvariant(identifier[0]) + identifier[1..];
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
