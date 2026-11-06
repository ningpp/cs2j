using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>Parameter passing mode part of a compat replacement signature.</summary>
public enum CompatParameterRefKind
{
    None,
    Ref,
    Out,
    In,
}

/// <summary>
/// One known-method parameter: the name is a placeholder token the replacement
/// body may reference (<c>utf16Source</c> in a C# body template maps to the
/// declaration's actual parameter name at rewrite time), and <see cref="TypeName"/>
/// is the namespace-less canonical type used for signature matching
/// (e.g. <c>ReadOnlySpan&lt;byte&gt;</c>, <c>int</c>).
/// </summary>
public sealed record CompatMethodParameter(
    string Name,
    string TypeName,
    CompatParameterRefKind RefKind = CompatParameterRefKind.None);

/// <summary>
/// One entry of the known-method replacement table: a fully-qualified method
/// signature plus the managed body that replaces the original implementation.
/// <para>
/// Matching is symbol based (namespace + containing type + method name +
/// parameter types + parameter ref kinds), so a same-named overload or a
/// same-named type in another namespace never matches. The declared namespace
/// is compared after the cs2j <c>System</c> → <c>dotnet</c> namespace rename
/// (see <see cref="NormalizeNamespace"/>), which lets the built-in
/// <c>System.Text.Json</c> entry match the intermediate tree's
/// <c>dotnet.text.json</c> copy while still rejecting unrelated namespaces.
/// </para>
/// <para>
/// <see cref="BodyTemplate"/> is a list of statements (no enclosing braces).
/// Parameter names declared in <see cref="Parameters"/> are substituted with the
/// actual parameter names of the matched declaration, so a rename of the
/// parameters cannot break the generated body. Extending the table is a
/// one-entry change: add a <see cref="CompatMethodReplacementEntry"/> to
/// <see cref="CompatMethodReplacementRegistry.Default"/> (or build a custom
/// registry with <see cref="CompatMethodReplacementRegistry.WithAdditionalEntries"/>
/// and pass it through <see cref="CompatMethodReplacementOptions.Registry"/>).
/// </para>
/// </summary>
public sealed record CompatMethodReplacementEntry
{
    /// <summary>Fully-qualified signature key, used for diagnostics and reporting.</summary>
    public required string Key { get; init; }

    /// <summary>Declaring namespace of the known method (source namespace, e.g. <c>System.Text.Json</c>).</summary>
    public required string ContainingNamespace { get; init; }

    /// <summary>Simple declaring type name (e.g. <c>JsonWriterHelper</c>).</summary>
    public required string ContainingTypeName { get; init; }

    /// <summary>Method name (e.g. <c>ToUtf8</c>).</summary>
    public required string MethodName { get; init; }

    /// <summary>Canonical return type name; null skips the return-type check.</summary>
    public string? ReturnTypeName { get; init; }

    /// <summary>Expected parameters in declaration order.</summary>
    public required IReadOnlyList<CompatMethodParameter> Parameters { get; init; }

    /// <summary>Replacement statements (the pass wraps them in a method body block).</summary>
    public required string BodyTemplate { get; init; }

    /// <summary>Whether the known method must be static (defaults to true).</summary>
    public bool RequiresStatic { get; init; } = true;

    /// <summary>Human-readable note shown in verbose diagnostics.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cheap identity check (type name + method name) used to recognize near
    /// misses so a signature/namespace mismatch can be reported as CMR001.
    /// </summary>
    public bool MatchesDeclarationIdentity(IMethodSymbol method)
        => string.Equals(method.Name, MethodName, StringComparison.Ordinal)
           && string.Equals(method.ContainingType?.Name, ContainingTypeName, StringComparison.Ordinal);

    /// <summary>
    /// Normalized <c>namespace|type</c> key of this entry's declaring type —
    /// the drift-detection evidence unit ("the type exists in the corpus").
    /// Shares the <c>dotnet → System</c> normalization with
    /// <see cref="Matches"/> so intermediate-tree copies match source-tree
    /// declarations.
    /// </summary>
    public string DeclaredTypeKey
        => $"{NormalizeNamespace(ContainingNamespace)}|{ContainingTypeName}";

    /// <summary>
    /// Family key (namespace + type + method name): entries of one family are
    /// alternate signatures of the SAME known method (e.g. the Runtime 10
    /// 3-param <c>ToUtf8</c> and the legacy 3.x 4-param form). Drift is judged
    /// per family — a matched sibling signature means the family is healthy,
    /// not drifted.
    /// </summary>
    public string FamilyKey
        => $"{NormalizeNamespace(ContainingNamespace)}|{ContainingTypeName}|{MethodName}";

    /// <summary>
    /// Computes the <see cref="DeclaredTypeKey"/> form for an actual method
    /// declaration's containing type (same normalization), so the rewriter's
    /// observations and the entries' keys stay one computation.
    /// </summary>
    public static string TypeKeyFor(IMethodSymbol method)
    {
        var ns = method.ContainingType?.ContainingNamespace;
        var nsName = ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
        return $"{NormalizeNamespace(nsName)}|{method.ContainingType?.Name}";
    }

    /// <summary>
    /// Full symbol match: namespace, containing type, name, static-ness,
    /// parameter count/types/ref-kinds and (when declared) the return type.
    /// </summary>
    public bool Matches(IMethodSymbol method, out string? mismatchReason)
    {
        mismatchReason = null;

        if (method.MethodKind != MethodKind.Ordinary)
        {
            mismatchReason = $"the method kind is {method.MethodKind}, not an ordinary method.";
            return false;
        }

        if (!string.Equals(method.Name, MethodName, StringComparison.Ordinal))
        {
            mismatchReason = $"the method name is '{method.Name}', not '{MethodName}'.";
            return false;
        }

        if (!string.Equals(method.ContainingType?.Name, ContainingTypeName, StringComparison.Ordinal))
        {
            mismatchReason =
                $"the declaring type is '{method.ContainingType?.Name}', not '{ContainingTypeName}'.";
            return false;
        }

        var declaringNamespace = GetNamespaceName(method);
        if (!NamespacesMatch(ContainingNamespace, declaringNamespace))
        {
            mismatchReason =
                $"the declaring namespace is '{declaringNamespace}', not '{ContainingNamespace}'.";
            return false;
        }

        if (RequiresStatic && !method.IsStatic)
        {
            mismatchReason = "the method is not static.";
            return false;
        }

        if (method.IsGenericMethod || method.TypeParameters.Length != 0)
        {
            mismatchReason = "the method declares type parameters.";
            return false;
        }

        if (method.Parameters.Length != Parameters.Count)
        {
            mismatchReason =
                $"the method has {method.Parameters.Length} parameter(s), not {Parameters.Count}.";
            return false;
        }

        for (var i = 0; i < Parameters.Count; i++)
        {
            var expected = Parameters[i];
            var actual = method.Parameters[i];
            var actualRefKind = ToCompatRefKind(actual.RefKind);
            if (actualRefKind != expected.RefKind)
            {
                mismatchReason =
                    $"parameter {i} ('{actual.Name}') has ref kind {actualRefKind}, not {expected.RefKind}.";
                return false;
            }

            var actualTypeName = CompatTypeName.Canonical(actual.Type);
            if (!string.Equals(actualTypeName, expected.TypeName, StringComparison.Ordinal))
            {
                mismatchReason =
                    $"parameter {i} ('{actual.Name}') is '{actualTypeName}', not '{expected.TypeName}'.";
                return false;
            }
        }

        if (ReturnTypeName is not null
            && !string.Equals(
                CompatTypeName.Canonical(method.ReturnType),
                ReturnTypeName,
                StringComparison.Ordinal))
        {
            mismatchReason =
                $"the return type is '{CompatTypeName.Canonical(method.ReturnType)}', not '{ReturnTypeName}'.";
            return false;
        }

        return true;
    }

    /// <summary>Diagnostic form of an actual declaration, for CMR001 messages.</summary>
    public static string Describe(IMethodSymbol method)
    {
        var parameters = string.Join(
            ", ",
            method.Parameters.Select(parameter =>
            {
                var prefix = ToCompatRefKind(parameter.RefKind) switch
                {
                    CompatParameterRefKind.Ref => "ref ",
                    CompatParameterRefKind.Out => "out ",
                    CompatParameterRefKind.In => "in ",
                    _ => string.Empty,
                };
                return prefix + CompatTypeName.Canonical(parameter.Type);
            }));
        return $"{GetNamespaceName(method)}.{method.ContainingType?.Name}.{method.Name}({parameters})";
    }

    private static string GetNamespaceName(IMethodSymbol method)
    {
        var ns = method.ContainingType?.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    private static CompatParameterRefKind ToCompatRefKind(RefKind refKind)
        => refKind switch
        {
            RefKind.Ref => CompatParameterRefKind.Ref,
            RefKind.Out => CompatParameterRefKind.Out,
            RefKind.In => CompatParameterRefKind.In,
            _ => CompatParameterRefKind.None,
        };

    private static bool NamespacesMatch(string declared, string actual)
        => string.Equals(
            NormalizeNamespace(declared),
            NormalizeNamespace(actual),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// cs2j renames <c>System.*</c> namespaces to <c>dotnet.*</c> in the
    /// intermediate tree, so the leading <c>dotnet</c> segment is treated as
    /// <c>System</c> for matching purposes. Every later segment must still match
    /// exactly (case-insensitively), which keeps a same-named type in an
    /// unrelated namespace from matching.
    /// </summary>
    internal static string NormalizeNamespace(string ns)
    {
        var segments = ns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length > 0
            && string.Equals(segments[0], "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            segments[0] = "System";
        }

        return string.Join('.', segments);
    }
}

/// <summary>Namespace-less canonical type names used by the replacement table.</summary>
internal static class CompatTypeName
{
    public static string Canonical(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
        {
            return type.SpecialType switch
            {
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Byte => "byte",
                SpecialType.System_SByte => "sbyte",
                SpecialType.System_Int16 => "short",
                SpecialType.System_UInt16 => "ushort",
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_Int64 => "long",
                SpecialType.System_UInt64 => "ulong",
                SpecialType.System_Char => "char",
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Decimal => "decimal",
                SpecialType.System_Object => "object",
                SpecialType.System_String => "string",
                SpecialType.System_Void => "void",
                _ => type.Name,
            };
        }

        if (type is IArrayTypeSymbol array)
            return Canonical(array.ElementType) + "[]";

        if (type is IPointerTypeSymbol pointer)
            return Canonical(pointer.PointedAtType) + "*";

        if (type is INamedTypeSymbol named)
        {
            if (named.IsGenericType)
            {
                var arguments = string.Join(", ", named.TypeArguments.Select(Canonical));
                return named.Name + "<" + arguments + ">";
            }

            return named.Name;
        }

        return type.Name;
    }
}

/// <summary>
/// The known-method replacement table. The table is code-registered (the pass
/// itself never hardcodes a signature) and deliberately small: each entry is a
/// method whose original body is pointer/fixed-based C++-ish interop that Java
/// cannot consume, and whose managed <see cref="CompatMethodReplacementEntry.BodyTemplate"/>
/// produces equivalent output.
/// <para>
/// Extension points: add an entry here for a globally known BCL/mirror method,
/// or build a custom table at runtime:
/// <c>CompatMethodReplacementRegistry.Default.WithAdditionalEntries(entry)</c>
/// and assign it to <see cref="CompatMethodReplacementOptions.Registry"/>. A
/// future configuration-JSON entry point can deserialize the same
/// <see cref="CompatMethodReplacementEntry"/> shape without changing the pass.
/// </para>
/// </summary>
public sealed class CompatMethodReplacementRegistry
{
    private static readonly CompatMethodReplacementRegistry DefaultRegistry = new(
    [
        // Runtime 10 signature (W-09 fix, upgrade plan §6.4-R0): rt10 moved the
        // pointer transcoder to JsonWriterHelper.cs:291 as
        // ToUtf8(ReadOnlySpan<char>, Span<byte>, out int written) — the legacy
        // 3.x 4-param byte-span entry below matched nothing on rt10 and the
        // mismatch was only ever visible as a CMR001 near-miss warning. Both
        // signatures stay registered as one FAMILY (same namespace/type/
        // method), so each corpus generation gets its replacement and drift
        // detection (CMR020) judges the family as a whole.
        new CompatMethodReplacementEntry
        {
            Key = "System.Text.Json.JsonWriterHelper.ToUtf8(" +
                  "System.ReadOnlySpan<char>, System.Span<byte>, out int)",
            ContainingNamespace = "System.Text.Json",
            ContainingTypeName = "JsonWriterHelper",
            MethodName = "ToUtf8",
            ReturnTypeName = "OperationStatus",
            Parameters = new[]
            {
                new CompatMethodParameter("source", "ReadOnlySpan<char>"),
                new CompatMethodParameter("destination", "Span<byte>"),
                new CompatMethodParameter("written", "int", CompatParameterRefKind.Out),
            },
            BodyTemplate = """
                written = 0;
                var sourceArray = source.ToArray();
                var destArray = destination.ToArray();
                int invalidIndex = -1;
                for (int i = 0; i < sourceArray.Length; i++)
                {
                    char current = sourceArray[i];
                    if (current >= 0xD800 && current <= 0xDBFF)
                    {
                        if (i + 1 >= sourceArray.Length
                            || (sourceArray[i + 1] < 0xDC00 || sourceArray[i + 1] > 0xDFFF))
                        {
                            invalidIndex = i;
                            break;
                        }

                        i++;
                    }
                    else if (current >= 0xDC00 && current <= 0xDFFF)
                    {
                        invalidIndex = i;
                        break;
                    }
                }

                int charLimit = invalidIndex < 0 ? sourceArray.Length : invalidIndex;
                var encoder = global::System.Text.Encoding.UTF8.GetEncoder();
                encoder.Convert(sourceArray, 0, charLimit, destArray, 0, destArray.Length, true,
                    out int charsUsed, out int bytesUsed, out bool completed);
                global::System.MemoryExtensions.AsSpan(destArray, 0, bytesUsed).CopyTo(destination);
                written = bytesUsed;
                if (!completed)
                {
                    return global::System.Buffers.OperationStatus.DestinationTooSmall;
                }

                return invalidIndex < 0
                    ? global::System.Buffers.OperationStatus.Done
                    : global::System.Buffers.OperationStatus.InvalidData;
                """,
            Description =
                "Runtime 10 form (JsonWriterHelper.cs:291): replaces the " +
                "pointer/Utf8.FromUtf16 transcoder with " +
                "Encoding.UTF8.GetEncoder().Convert over materialized arrays, " +
                "stopping at the first unpaired surrogate (InvalidData) so the " +
                "valid prefix is written without replacement characters.",
        },
        // Legacy .NET 3.x-era signature (D:\\csharp-sdk\\json's
        // JsonWriterHelper.Transcoding.cs:28) kept as a family sibling so the
        // old corpus keeps its replacement until that corpus retires.
        new CompatMethodReplacementEntry
        {
            Key = "System.Text.Json.JsonWriterHelper.ToUtf8(" +
                  "System.ReadOnlySpan<byte>, System.Span<byte>, out int, out int)",
            ContainingNamespace = "System.Text.Json",
            ContainingTypeName = "JsonWriterHelper",
            MethodName = "ToUtf8",
            ReturnTypeName = "OperationStatus",
            Parameters = new[]
            {
                new CompatMethodParameter("utf16Source", "ReadOnlySpan<byte>"),
                new CompatMethodParameter("utf8Destination", "Span<byte>"),
                new CompatMethodParameter("bytesConsumed", "int", CompatParameterRefKind.Out),
                new CompatMethodParameter("bytesWritten", "int", CompatParameterRefKind.Out),
            },
            BodyTemplate = """
                bytesConsumed = 0;
                bytesWritten = 0;
                var chars = global::System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(utf16Source);
                var sourceArray = chars.ToArray();
                var destArray = utf8Destination.ToArray();
                int invalidIndex = -1;
                for (int i = 0; i < sourceArray.Length; i++)
                {
                    char current = sourceArray[i];
                    if (current >= 0xD800 && current <= 0xDBFF)
                    {
                        if (i + 1 >= sourceArray.Length
                            || (sourceArray[i + 1] < 0xDC00 || sourceArray[i + 1] > 0xDFFF))
                        {
                            invalidIndex = i;
                            break;
                        }

                        i++;
                    }
                    else if (current >= 0xDC00 && current <= 0xDFFF)
                    {
                        invalidIndex = i;
                        break;
                    }
                }

                int charLimit = invalidIndex < 0 ? sourceArray.Length : invalidIndex;
                var encoder = global::System.Text.Encoding.UTF8.GetEncoder();
                encoder.Convert(sourceArray, 0, charLimit, destArray, 0, destArray.Length, true,
                    out int charsUsed, out int bytesUsed, out bool completed);
                global::System.MemoryExtensions.AsSpan(destArray, 0, bytesUsed).CopyTo(utf8Destination);
                bytesConsumed = charsUsed * 2;
                bytesWritten = bytesUsed;
                if (!completed)
                {
                    return global::System.Buffers.OperationStatus.DestinationTooSmall;
                }

                return invalidIndex < 0
                    ? global::System.Buffers.OperationStatus.Done
                    : global::System.Buffers.OperationStatus.InvalidData;
                """,
            Description =
                "Replaces the fixed/pointer UTF-16→UTF-8 transcoder with " +
                "Encoding.UTF8.GetEncoder().Convert over materialized arrays, " +
                "stopping at the first unpaired surrogate (InvalidData) so the " +
                "valid prefix is written without replacement characters.",
        },
        new CompatMethodReplacementEntry
        {
            Key = "System.Text.Json.JsonWriterHelper.NeedsEscaping(" +
                  "System.ReadOnlySpan<char>, System.Text.Encodings.Web.JavaScriptEncoder)",
            ContainingNamespace = "System.Text.Json",
            ContainingTypeName = "JsonWriterHelper",
            MethodName = "NeedsEscaping",
            ReturnTypeName = "int",
            Parameters = new[]
            {
                new CompatMethodParameter("value", "ReadOnlySpan<char>"),
                new CompatMethodParameter("encoder", "JavaScriptEncoder"),
            },
            BodyTemplate = """
                if (value.IsEmpty)
                {
                    return -1;
                }

                var __encoder = object.ReferenceEquals(encoder, null)
                    ? global::System.Text.Encodings.Web.JavaScriptEncoder.Default
                    : encoder;
                int __i = 0;
                while (__i < value.Length)
                {
                    int __scalar = (int)value[__i];
                    if (__scalar >= 0xD800 && __scalar <= 0xDBFF)
                    {
                        if (__i + 1 < value.Length
                            && (int)value[__i + 1] >= 0xDC00
                            && (int)value[__i + 1] <= 0xDFFF)
                        {
                            __scalar = 0x10000 + ((__scalar - 0xD800) << 10)
                                + ((int)value[__i + 1] - 0xDC00);
                            if (__encoder.WillEncode(__scalar))
                            {
                                return __i;
                            }

                            __i += 2;
                            continue;
                        }

                        return __i;
                    }

                    if (__scalar >= 0xDC00 && __scalar <= 0xDFFF)
                    {
                        return __i;
                    }

                    if (__encoder.WillEncode(__scalar))
                    {
                        return __i;
                    }

                    __i++;
                }

                return -1;
                """,
            Description =
                "Replaces the char-span fixed/pointer scan " +
                "(FindFirstCharacterToEncode(char*, int)) with a managed " +
                "surrogate-aware WillEncode loop: a valid surrogate pair is " +
                "checked as its combined scalar (returning the high-surrogate " +
                "index), and an unpaired surrogate is reported as needing " +
                "escaping at its own index. The empty-span guard keeps the " +
                "original semantics for encoders that dereference the pointer " +
                "even when the length is zero.",
        },
    ]);

    public CompatMethodReplacementRegistry(IReadOnlyList<CompatMethodReplacementEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
    }

    /// <summary>
    /// The built-in table: <c>JsonWriterHelper.ToUtf8</c> and the char-span
    /// <c>JsonWriterHelper.NeedsEscaping</c> pointer scan.
    /// </summary>
    public static CompatMethodReplacementRegistry Default => DefaultRegistry;

    public IReadOnlyList<CompatMethodReplacementEntry> Entries { get; }

    /// <summary>Return a new registry with the given entries appended (extension seam).</summary>
    public CompatMethodReplacementRegistry WithAdditionalEntries(
        params CompatMethodReplacementEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length == 0)
            return this;

        return new CompatMethodReplacementRegistry(Entries.Concat(entries).ToArray());
    }
}
