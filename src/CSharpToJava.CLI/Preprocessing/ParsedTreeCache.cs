using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Process-wide cache of parsed C# syntax trees keyed by (parse-option signature →
/// absolute file path). It lives for the duration of one <c>convert-project</c> /
/// <c>preprocess</c> process so a unified-directory pass chain reads and parses the
/// whole tree once instead of re-parsing every .cs file on every pass: an in-place
/// chain only rewrites a small fraction of the tree per pass, so every other file's
/// bytes — and therefore its parse tree — are unchanged between passes.
///
/// Correctness:
/// <list type="bullet">
/// <item>Entries are partitioned by a deterministic signature of the adapter's
/// <see cref="CSharpParseOptions"/> (kind, language version, documentation mode,
/// preprocessor symbol set, feature flags). A tree parsed under one option set is
/// never handed to a pass using a different one, so the <c>NETCOREAPP</c> symbol and
/// language-version differences between adapters can never cross-contaminate
/// <c>#if</c> resolution or feature-dependent parsing.</item>
/// <item>An entry is reused only when the freshly read bytes hash identically to the
/// bytes the cached tree was parsed from; the file's mtime is never trusted on its
/// own (a snapshot restore or rollback can rewrite content without a reliable
/// timestamp). A changed file is reparsed and the entry refreshed.</item>
/// <item><see cref="SyntaxTree"/> is immutable: Roslyn allows one tree to feed many
/// compilations / semantic models and no pass mutates its parsed input trees, so
/// sharing an instance is observationally identical to re-parsing it.</item>
/// </list>
/// </summary>
internal static class ParsedTreeCache
{
    private sealed class Entry
    {
        public required ulong Hash { get; init; }
        public required SyntaxTree Tree { get; init; }
    }

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> s_byParseOptions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Deterministic signature covering every parse-option dimension that can change
    /// how a source file is parsed. Two adapters whose parse options yield the same
    /// signature parse trees identically and may share cache entries.
    /// </summary>
    public static string Signature(CSharpParseOptions parseOptions)
    {
        var builder = new StringBuilder(64);
        builder.Append((int)parseOptions.Kind).Append(';');
        builder.Append((int)parseOptions.LanguageVersion).Append(';');
        builder.Append((int)parseOptions.DocumentationMode).Append(';');
        foreach (var symbol in parseOptions.PreprocessorSymbolNames.OrderBy(s => s, StringComparer.Ordinal))
        {
            builder.Append(symbol).Append(',');
        }

        builder.Append(';');
        foreach (var feature in parseOptions.Features.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            builder.Append(feature.Key).Append('=').Append(feature.Value).Append(',');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the cached tree when the file was already parsed under the same parse
    /// options from byte-identical content; otherwise returns false (caller reparses).
    /// </summary>
    public static bool TryGet(string parseSignature, string filePath, string sourceCode, out SyntaxTree tree)
    {
        tree = null!;
        if (!s_byParseOptions.TryGetValue(parseSignature, out var byPath)
            || !byPath.TryGetValue(filePath, out var entry)
            || entry.Hash != Hash(sourceCode))
        {
            return false;
        }

        tree = entry.Tree;
        return true;
    }

    /// <summary>Stores (or refreshes) the parsed tree so it can be reused later.</summary>
    public static void Store(string parseSignature, string filePath, string sourceCode, SyntaxTree tree)
    {
        var byPath = s_byParseOptions.GetOrAdd(
            parseSignature,
            static _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        byPath[filePath] = new Entry { Hash = Hash(sourceCode), Tree = tree };
    }

    /// <summary>64-bit FNV-1a over the UTF-16 code units of the source text.</summary>
    private static ulong Hash(string text)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var ch in text)
        {
            hash = (hash ^ ch) * prime;
        }

        return hash;
    }
}
