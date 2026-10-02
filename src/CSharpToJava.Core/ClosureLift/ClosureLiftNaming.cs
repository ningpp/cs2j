using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Deterministic naming (design §4.6):
///   class    Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;  — n per host type, assigned in
///            the syntax order of groups (SpanStart); cross-file table makes
///            partial parts of one host type share the reservation set.
///   instance __cs2jClosure&lt;n&gt;                   — n per enclosing routine;
///            a user-local collision gets _2, _3 ... (ResolveUniqueName style).
///   field    _cs2jCap_&lt;originalName&gt;           — _2, _3 ... within one class.
/// No randomness/hashes: every decision derives from syntax order + symbols.
/// One instance is reused across files in project mode (mirrors E02).
/// </summary>
internal sealed class ClosureLiftNaming
{
    // hostTypeKey (fully qualified display) -> fileKey -> class names reserved by that file.
    // Buckets are per file so that (a) partial parts of one host type in different
    // files share the union of reservations (continuation), while (b) re-entering the
    // same file (idempotent second pass / text round-trip) replaces that file's bucket
    // and yields the same names instead of advancing the index.
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _classNamesByHostByFile =
        new(StringComparer.Ordinal);

    private string _currentFileKey = string.Empty;

    /// <summary>
    /// Begin processing one file. Re-entering the same file key drops its previous
    /// reservation buckets across every host; reservations from other files persist.
    /// The key should distinguish files (path) and contents (round-trip identity):
    /// callers pass <c>tree.FilePath + '\0' + sourceCode</c>.
    /// </summary>
    public void BeginFile(string fileKey)
    {
        _currentFileKey = fileKey;
        foreach (var perFile in _classNamesByHostByFile.Values)
            perFile.Remove(fileKey);
    }

    /// <summary>Reserve Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt; for one group inside a host type.</summary>
    public string ReserveClassName(string hostTypeKey, string hostMemberName)
    {
        if (!_classNamesByHostByFile.TryGetValue(hostTypeKey, out var perFile))
        {
            perFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _classNamesByHostByFile[hostTypeKey] = perFile;
        }

        if (!perFile.TryGetValue(_currentFileKey, out var currentFileUsed))
        {
            currentFileUsed = new HashSet<string>(StringComparer.Ordinal);
            perFile[_currentFileKey] = currentFileUsed;
        }

        // Union across every file: partial parts and this file's fresh bucket.
        var usedAcrossFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in perFile.Values)
            usedAcrossFiles.UnionWith(bucket);

        var memberPart = SanitizeMemberName(hostMemberName);
        for (var n = 0; ; n++)
        {
            var candidate = $"Cs2jDisplayClass_{memberPart}_{n}";
            if (usedAcrossFiles.Add(candidate))
            {
                currentFileUsed.Add(candidate);
                return candidate;
            }
        }
    }

    /// <summary>
    /// Reserve the per-routine instance name. <paramref name="localNamesInRoutine"/>
    /// starts with every existing local/parameter name of the routine and gains
    /// each generated name, so user-named __cs2jClosure* locals cannot collide.
    /// </summary>
    public static string ReserveInstanceName(int groupIndex, ISet<string> localNamesInRoutine)
    {
        var candidate = $"__cs2jClosure{groupIndex}";
        if (localNamesInRoutine.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (localNamesInRoutine.Add(suffixed))
                return suffixed;
        }
    }

    /// <summary>Reserve the public field name inside one holder class.</summary>
    public static string GetFieldName(string originalName, ISet<string> usedFieldNames)
    {
        var candidate = "_cs2jCap_" + TrimEscape(originalName);
        if (usedFieldNames.Add(candidate))
            return candidate;

        for (var suffix = 2; ; suffix++)
        {
            var suffixed = $"{candidate}_{suffix}";
            if (usedFieldNames.Add(suffixed))
                return suffixed;
        }
    }

    /// <summary>Non-default array-shape local name (design §4.4): __cs2jArr_&lt;name&gt;.</summary>
    public static string GetArrayLocalName(string originalName)
        => "__cs2jArr_" + TrimEscape(originalName);

    /// <summary>
    /// The class-name segment keeps the Pascal spelling of the host member.
    /// A member literally named with a C# reserved keyword (e.g. <c>@class</c>)
    /// gets a deterministic Member suffix. Contextual keywords stay verbatim
    /// (they are legal identifiers, same policy as E02 GetBaseName).
    /// </summary>
    internal static string SanitizeMemberName(string name)
    {
        var stripped = TrimEscape(name);
        return SyntaxFacts.GetKeywordKind(stripped) != SyntaxKind.None
            ? stripped + "Member"
            : stripped;
    }

    private static string TrimEscape(string name)
        => name.StartsWith("@", StringComparison.Ordinal) && name.Length > 1 ? name[1..] : name;
}
