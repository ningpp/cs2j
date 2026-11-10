using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.BigLiteralResource;

/// <summary>
/// Lexical pre-pass <c>big-literal-resource</c>: moves oversized literal payloads
/// out of a C# source file into per-payload resource files and replaces them with
/// a loader call.
/// <para>
/// Scope (deliberately syntactic — the pass runs second in the chain, before any
/// semantic pass, so it must not need a <see cref="SemanticModel"/>):
/// </para>
/// <list type="bullet">
/// <item>field and local declarations of a one-dimensional array whose element
/// type is <c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c>/<c>float</c>/
/// <c>double</c>/<c>char</c>/<c>bool</c>/<c>string</c>, initialized with
/// <c>new T[] { … }</c>, <c>new[] { … }</c> or a bare <c>{ … }</c> initializer
/// where <em>every</em> element is a literal (optionally wrapped in a numeric
/// cast);</item>
/// <item>field and local declarations of a <c>string</c> initialized with a single
/// literal larger than <see cref="BigLiteralResourceOptions.MinStringPayloadBytes"/>.</item>
/// </list>
/// <para>
/// Everything else is left untouched on purpose: <c>const</c> declarations (a
/// method call is not a constant expression, and without a semantic model the
/// pass cannot prove no attribute/case-label/default-argument use exists),
/// multi-dimensional and jagged arrays, initializers mixing literals with
/// non-literals, and <c>sbyte</c>/<c>ushort</c>/<c>uint</c>/<c>ulong</c> arrays
/// (owned later by the <c>unsigned</c> pass).
/// </para>
/// <para>
/// Clean files are returned byte-for-byte, and re-running the pass on its own
/// output is a no-op (the literal is gone, so nothing matches) — the payload name
/// is content-addressed, so a second run could never produce a different file.
/// </para>
/// </summary>
public sealed class BigLiteralResourceExtractor
{
    private readonly BigLiteralResourceOptions _options;

    public BigLiteralResourceExtractor(BigLiteralResourceOptions? options = null)
    {
        _options = options ?? new BigLiteralResourceOptions();
    }

    /// <summary>
    /// One measured literal declaration. Collected before any decision so the
    /// per-file budget can be evaluated against the whole file.
    /// </summary>
    private sealed record Candidate(
        VariableDeclaratorSyntax Declarator,
        ExpressionSyntax Value,
        BigLiteralElementKind Kind,
        bool IsArray,
        byte[] Payload,
        bool IsConst,
        int Line);

    /// <summary>Extract every qualifying literal of one file.</summary>
    /// <param name="sourceCode">Full file text.</param>
    /// <param name="relativePath">Relative path; used for parse-tree identity and diagnostics only.</param>
    public BigLiteralResourceResult RewriteFile(string sourceCode, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);

        var statistics = new BigLiteralResourceStatistics { FilesScanned = 1 };
        var diagnostics = new List<BigLiteralResourceDiagnostic>();

        if (!_options.Enabled || sourceCode.Length == 0)
        {
            return Unchanged(sourceCode, statistics, diagnostics);
        }

        var root = ParseRoot(sourceCode, relativePath);

        // The rewrite replaces single expressions inside the parse tree and
        // replays the rest verbatim. If the tree cannot round-trip the file
        // byte-for-byte (exotic trivia the parser normalizes), the pass must not
        // touch the file at all — a lexical pass may never reformat code it did
        // not intend to change.
        if (!string.Equals(root.ToFullString(), sourceCode, StringComparison.Ordinal))
        {
            return Unchanged(sourceCode, statistics, diagnostics);
        }

        // ---- phase 1: collect every candidate, decide nothing yet ----
        //
        // The decision is two-tier (see BigLiteralResourceOptions.MaxInlinePayloadBytes):
        // a single oversized literal always moves, and additionally the whole
        // file is emptied out once its movable literal payload exceeds the inline
        // budget. That is why the scan cannot decide while walking: the file total
        // is only known after every candidate is measured.
        var candidates = new List<Candidate>();
        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer is not { } initializer
                || declarator.Parent is not VariableDeclarationSyntax declaration
                || declaration.Parent is not (FieldDeclarationSyntax or LocalDeclarationStatementSyntax))
            {
                // Event fields, for-loop declarations, deconstruction and
                // declaration expressions are not worth the risk: they either
                // cannot hold an array literal or are rewritten by later passes.
                continue;
            }

            var isConst =
                (declaration.Parent as FieldDeclarationSyntax)?.Modifiers
                    .Any(token => token.IsKind(SyntaxKind.ConstKeyword)) == true
                || (declaration.Parent as LocalDeclarationStatementSyntax)?.Modifiers
                    .Any(token => token.IsKind(SyntaxKind.ConstKeyword)) == true;

            var value = initializer.Value;
            if (!TryResolveElementKind(declaration, value, out var kind, out var isArray))
            {
                continue;
            }

            var line = declarator.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            if (isArray)
            {
                if (!TryGetArrayElements(value, out var elements))
                {
                    continue;
                }

                if (!TryEncodeArray(kind, elements, out var content, out var allLiteral))
                {
                    if (!allLiteral)
                    {
                        statistics.SkippedNonLiteral++;
                        diagnostics.Add(new BigLiteralResourceDiagnostic(
                            BigLiteralResourceSeverity.Info,
                            Preprocessing.PreprocessDiagnosticCodes.BigLiteralNonLiteralElements,
                            $"array '{declarator.Identifier.ValueText}' mixes literals with " +
                            "non-literal elements and stays inline",
                            line));
                    }

                    continue;
                }

                if (content!.Length < _options.MinCandidatePayloadBytes)
                {
                    continue;
                }

                candidates.Add(new Candidate(
                    declarator, value, kind, IsArray: true, content, isConst, line));
                continue;
            }

            if (value is not LiteralExpressionSyntax literal
                || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }

            var text = literal.Token.ValueText;
            if (text.Length == 0)
            {
                continue;
            }

            if (HasLoneSurrogate(text))
            {
                statistics.SkippedUnsupportedText++;
                diagnostics.Add(new BigLiteralResourceDiagnostic(
                    BigLiteralResourceSeverity.Warning,
                    Preprocessing.PreprocessDiagnosticCodes.BigLiteralUnpairedSurrogate,
                    $"string '{declarator.Identifier.ValueText}' contains an unpaired surrogate " +
                    "and stays inline (a UTF-8 round-trip would not be lossless)",
                    line));
                continue;
            }

            var stringContent = Encoding.UTF8.GetBytes(text);
            if (stringContent.Length < _options.MinCandidatePayloadBytes)
            {
                continue;
            }

            candidates.Add(new Candidate(
                declarator, value, BigLiteralElementKind.String, IsArray: false,
                stringContent, isConst, line));
        }

        // const declarations can never move (a loader call is not a constant
        // expression), so they must not inflate the budget the other literals
        // are measured against.
        var movableTotal = candidates
            .Where(candidate => !candidate.IsConst)
            .Sum(candidate => (long)candidate.Payload.Length);
        var overBudget = movableTotal > _options.MaxInlinePayloadBytes;

        // ---- phase 2: decide + rewrite ----
        var replacements = new Dictionary<ExpressionSyntax, ExpressionSyntax>();
        var payloads = new List<BigLiteralResourcePayload>();
        var emittedNames = new HashSet<string>(StringComparer.Ordinal);
        var constCount = 0;
        var constBytes = 0L;
        var constSample = string.Empty;
        var extractedCount = 0;
        var extractedBytes = 0L;

        foreach (var candidate in candidates)
        {
            var alwaysExternalize = candidate.Payload.Length
                >= (candidate.IsArray ? _options.MinArrayPayloadBytes : _options.MinStringPayloadBytes);
            if (!alwaysExternalize && !overBudget)
            {
                continue;
            }

            if (candidate.IsConst)
            {
                // Report the ones that WOULD have moved; the rest are ordinary
                // constant tables the pass never wanted.
                constCount++;
                constBytes += candidate.Payload.Length;
                if (constSample.Length == 0)
                {
                    constSample = candidate.Declarator.Identifier.ValueText;
                }

                continue;
            }

            var (resourceName, fileName) = BuildResourceName(
                candidate.Declarator, candidate.Kind, candidate.IsArray, candidate.Payload);
            if (emittedNames.Add(resourceName))
            {
                payloads.Add(new BigLiteralResourcePayload
                {
                    ResourceName = resourceName,
                    FileName = fileName,
                    Content = candidate.Payload,
                    SourceSymbol = DeclaringSymbol(candidate.Declarator),
                });
            }

            replacements[candidate.Value] = BuildLoaderCall(
                candidate.IsArray
                    ? BigLiteralElementKinds.ArrayLoaderMethod(candidate.Kind)
                    : BigLiteralElementKinds.StringLoaderMethod,
                resourceName,
                candidate.Value);

            if (candidate.IsArray)
            {
                statistics.ArraysExtracted++;
            }
            else
            {
                statistics.StringsExtracted++;
            }

            statistics.PayloadBytes += candidate.Payload.Length;
            extractedCount++;
            extractedBytes += candidate.Payload.Length;
        }

        if (overBudget)
        {
            statistics.FilesOverBudget++;
        }

        statistics.SkippedConst += constCount;
        if (constCount > 0)
        {
            diagnostics.Add(new BigLiteralResourceDiagnostic(
                BigLiteralResourceSeverity.Warning,
                Preprocessing.PreprocessDiagnosticCodes.BigLiteralConstDeclaration,
                $"{constCount} const declaration(s) ({constBytes} bytes, e.g. '{constSample}') " +
                "cannot be externalized: a loader call is not a constant expression",
                candidates.First(candidate => candidate.IsConst).Line));
        }

        if (_options.ReportExtractions && extractedCount > 0)
        {
            diagnostics.Add(new BigLiteralResourceDiagnostic(
                BigLiteralResourceSeverity.Info,
                Preprocessing.PreprocessDiagnosticCodes.BigLiteralExtracted,
                $"{extractedCount} literal payload(s) externalized ({extractedBytes} bytes)" +
                (overBudget
                    ? $"; file budget tripped: {movableTotal} > {_options.MaxInlinePayloadBytes} bytes"
                    : string.Empty),
                0));
        }

        if (replacements.Count == 0)
        {
            return Unchanged(sourceCode, statistics, diagnostics);
        }

        var output = root
            .ReplaceNodes(replacements.Keys, (original, _) => replacements[original])
            .ToFullString();

        statistics.FilesTransformed++;
        return new BigLiteralResourceResult
        {
            OutputCode = output,
            Changed = true,
            Payloads = payloads,
            Diagnostics = diagnostics,
            Statistics = statistics,
        };
    }

    private static BigLiteralResourceResult Unchanged(
        string sourceCode,
        BigLiteralResourceStatistics statistics,
        List<BigLiteralResourceDiagnostic> diagnostics)
    {
        statistics.FilesSkippedClean++;
        return new BigLiteralResourceResult
        {
            OutputCode = sourceCode,
            Changed = false,
            Payloads = Array.Empty<BigLiteralResourcePayload>(),
            Diagnostics = diagnostics,
            Statistics = statistics,
        };
    }

    private static SyntaxNode ParseRoot(string sourceCode, string relativePath)
        => CSharpSyntaxTree
            .ParseText(sourceCode, new CSharpParseOptions(LanguageVersion.Latest), path: relativePath)
            .GetRoot();

    /// <summary>
    /// Resolve the declared type to an element kind and say whether the
    /// declaration is an array (true) or a scalar <c>string</c> (false).
    /// </summary>
    private static bool TryResolveElementKind(
        VariableDeclarationSyntax declaration,
        ExpressionSyntax value,
        out BigLiteralElementKind kind,
        out bool isArray)
    {
        kind = default;
        isArray = false;

        if (declaration.Type is ArrayTypeSyntax arrayType)
        {
            // Only rank-1 arrays: a jagged/multi-dimensional payload has no
            // single flat byte layout the loader could rebuild.
            if (arrayType.RankSpecifiers.Count != 1
                || arrayType.RankSpecifiers[0].Rank != 1)
            {
                return false;
            }

            isArray = true;
            return BigLiteralElementKinds.TryResolve(arrayType.ElementType.ToString(), out kind);
        }

        var isVar = declaration.Type is IdentifierNameSyntax identifier && identifier.IsVar;
        if (isVar)
        {
            // `var data = new byte[] { … }` — the array creation carries the type.
            if (value is ArrayCreationExpressionSyntax dynamicArray
                && dynamicArray.Type.RankSpecifiers.Count == 1
                && dynamicArray.Type.RankSpecifiers[0].Rank == 1)
            {
                isArray = true;
                return BigLiteralElementKinds.TryResolve(
                    dynamicArray.Type.ElementType.ToString(), out kind);
            }

            return false;
        }

        if (!BigLiteralElementKinds.TryResolve(declaration.Type.ToString(), out kind))
        {
            return false;
        }

        if (kind == BigLiteralElementKind.String)
        {
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>Extract the element list of the three supported initializer shapes.</summary>
    private static bool TryGetArrayElements(
        ExpressionSyntax value,
        out SeparatedSyntaxList<ExpressionSyntax> elements)
    {
        switch (value)
        {
            case ArrayCreationExpressionSyntax creation
                when creation.Type.RankSpecifiers.Count == 1
                     && creation.Type.RankSpecifiers[0].Rank == 1
                     && creation.Initializer != null:
                elements = creation.Initializer.Expressions;
                return true;

            case ImplicitArrayCreationExpressionSyntax implicitCreation:
                elements = implicitCreation.Initializer.Expressions;
                return true;

            case InitializerExpressionSyntax initializer
                when initializer.IsKind(SyntaxKind.ArrayInitializerExpression):
                elements = initializer.Expressions;
                return true;

            default:
                elements = default;
                return false;
        }
    }

    /// <summary>
    /// Encode the element list into the payload bytes. Returns false when an
    /// element is not a literal; <paramref name="content"/> is then null and
    /// <paramref name="allLiteral"/> reports why.
    /// </summary>
    private static bool TryEncodeArray(
        BigLiteralElementKind kind,
        SeparatedSyntaxList<ExpressionSyntax> elements,
        out byte[]? content,
        out bool allLiteral)
    {
        content = null;
        allLiteral = true;

        var width = BigLiteralElementKinds.ElementWidth(kind);
        using var buffer = new MemoryStream(
            kind == BigLiteralElementKind.String
                ? 1024
                : Math.Max(16, elements.Count * width));

        foreach (var element in elements)
        {
            switch (kind)
            {
                case BigLiteralElementKind.String:
                {
                    if (!TryGetStringValue(element, out var text))
                    {
                        allLiteral = false;
                        break;
                    }

                    var bytes = Encoding.UTF8.GetBytes(text);
                    WriteInt32LittleEndian(buffer, bytes.Length);
                    buffer.Write(bytes, 0, bytes.Length);
                    break;
                }

                case BigLiteralElementKind.Single:
                {
                    if (!TryGetDoubleValue(element, out var value))
                    {
                        allLiteral = false;
                        break;
                    }

                    var bits = BitConverter.SingleToInt32Bits((float)value);
                    WriteSlot(buffer, unchecked((ulong)(long)bits), 4);
                    break;
                }

                case BigLiteralElementKind.Double:
                {
                    if (!TryGetDoubleValue(element, out var value))
                    {
                        allLiteral = false;
                        break;
                    }

                    WriteSlot(buffer, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), 8);
                    break;
                }

                default:
                {
                    if (!TryGetIntegralValue(element, out var value))
                    {
                        allLiteral = false;
                        break;
                    }

                    WriteSlot(buffer, value, width);
                    break;
                }
            }
        }

        if (!allLiteral)
        {
            return false;
        }

        content = buffer.ToArray();
        return true;
    }

    private static void WriteSlot(Stream stream, ulong value, int width)
    {
        Span<byte> slot = stackalloc byte[width];
        for (var index = 0; index < width; index++)
        {
            slot[index] = (byte)(value >> (8 * index));
        }

        stream.Write(slot);
    }

    private static void WriteInt32LittleEndian(Stream stream, int value)
    {
        Span<byte> slot = stackalloc byte[4];
        for (var index = 0; index < 4; index++)
        {
            slot[index] = (byte)((uint)value >> (8 * index));
        }

        stream.Write(slot);
    }

    /// <summary>
    /// Read an integral element as an unsigned 64-bit two's-complement slot, so
    /// signed values, hex literals and unchecked casts all truncate exactly the
    /// way the C# compiler does.
    /// </summary>
    private static bool TryGetIntegralValue(ExpressionSyntax expression, out ulong value)
    {
        value = 0;

        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                value = 1;
                return true;

            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                value = 0;
                return true;

            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression):
                value = literal.Token.Value is char ch ? ch : 0UL;
                return true;

            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                return TryParseIntegerToken(literal.Token.Text, out value);

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression):
                if (TryGetIntegralValue(unary.Operand, out var negated))
                {
                    value = unchecked(0UL - negated);
                    return true;
                }

                return false;

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryPlusExpression):
                return TryGetIntegralValue(unary.Operand, out value);

            case CastExpressionSyntax cast when BigLiteralElementKinds.TryResolve(
                    cast.Type.ToString(), out var castKind)
                && BigLiteralElementKinds.IsFixedWidth(castKind):
                // `(byte)0x80` / `(int)-1`: the cast only truncates, which the
                // slot writer does for us.
                return TryGetIntegralValue(cast.Expression, out value);

            default:
                return false;
        }
    }

    private static bool TryGetDoubleValue(ExpressionSyntax expression, out double value)
    {
        value = 0;

        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                return TryParseFloatingToken(literal.Token.Text, out value);

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression):
                if (TryGetDoubleValue(unary.Operand, out var negated))
                {
                    value = -negated;
                    return true;
                }

                return false;

            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryPlusExpression):
                return TryGetDoubleValue(unary.Operand, out value);

            case CastExpressionSyntax cast:
                return TryGetDoubleValue(cast.Expression, out value);

            default:
                return false;
        }
    }

    private static bool TryGetStringValue(ExpressionSyntax expression, out string value)
    {
        value = string.Empty;
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return !HasLoneSurrogate(value);
        }

        return false;
    }

    /// <summary>
    /// Parse a C# integer literal token (<c>0x</c>/<c>0b</c>/decimal, <c>_</c>
    /// separators, <c>u</c>/<c>l</c> suffixes) into its two's-complement slot.
    /// </summary>
    private static bool TryParseIntegerToken(string tokenText, out ulong value)
    {
        value = 0;
        var text = tokenText.Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
        {
            return false;
        }

        // Strip the type suffix (u/U/l/L in any order and combination).
        var end = text.Length;
        while (end > 0 && text[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            end--;
        }

        var digits = text[..end];
        if (digits.Length == 0)
        {
            return false;
        }

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(
                digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            var binary = digits[2..];
            value = 0;
            foreach (var ch in binary)
            {
                if (ch is not ('0' or '1'))
                {
                    return false;
                }

                value = (value << 1) | (uint)(ch - '0');
            }

            return binary.Length > 0;
        }

        return ulong.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parse a C# floating-point literal token into a <see cref="double"/>.</summary>
    private static bool TryParseFloatingToken(string tokenText, out double value)
    {
        value = 0;
        var text = tokenText.Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
        {
            return false;
        }

        var end = text.Length;
        while (end > 0 && text[end - 1] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M')
        {
            end--;
        }

        var digits = text[..end];
        if (digits.Length == 0)
        {
            return false;
        }

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (char.IsHighSurrogate(ch))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return true;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Build the loader call, preserving the replaced node's trivia.</summary>
    private ExpressionSyntax BuildLoaderCall(
        string loaderMethod,
        string resourceName,
        ExpressionSyntax replaced)
    {
        var call = ParseExpression(
            $"{_options.LoaderTypeName}.{loaderMethod}(\"{resourceName}\")");

        // Keep the original leading trivia (indentation, comments) so the
        // rewritten expression stays exactly where the developer wrote it.
        return call
            .WithLeadingTrivia(replaced.GetLeadingTrivia())
            .WithTrailingTrivia(replaced.GetTrailingTrivia());
    }

    /// <summary>
    /// Content-addressed resource name:
    /// <c>cs2j/&lt;Owner&gt;.&lt;Member&gt;.&lt;hash8&gt;.&lt;ext&gt;</c>.
    /// The hash keeps the name stable across runs, so a re-run cannot produce a
    /// differently named payload for the same source.
    /// </summary>
    private (string ResourceName, string FileName) BuildResourceName(
        VariableDeclaratorSyntax declarator,
        BigLiteralElementKind kind,
        bool isArray,
        byte[] content)
    {
        var owner = BuildOwnerPath(declarator);
        var member = Sanitize(declarator.Identifier.ValueText);
        var hash = Convert.ToHexString(SHA256.HashData(content))[..8].ToLowerInvariant();
        var extension = kind == BigLiteralElementKind.String && !isArray ? ".txt" : ".bin";
        var fileName = $"{owner}.{member}.{hash}{extension}";
        return (_options.LogicalNamePrefix + fileName, fileName);
    }

    private static string DeclaringSymbol(VariableDeclaratorSyntax declarator)
        => BuildOwnerPath(declarator) + "." + declarator.Identifier.ValueText;

    /// <summary>
    /// <c>Outer.Inner.Method</c> for a local, <c>Outer.Inner</c> for a field.
    /// Purely syntactic (no semantic model at this chain position).
    /// </summary>
    private static string BuildOwnerPath(VariableDeclaratorSyntax declarator)
    {
        var types = new List<string>();
        string? member = null;

        foreach (var ancestor in declarator.Ancestors())
        {
            switch (ancestor)
            {
                case TypeDeclarationSyntax type:
                    types.Add(type.Identifier.ValueText);
                    break;
                case MethodDeclarationSyntax method when member == null:
                    member = method.Identifier.ValueText;
                    break;
                case ConstructorDeclarationSyntax constructor when member == null:
                    member = constructor.Identifier.ValueText;
                    break;
                case DestructorDeclarationSyntax when member == null:
                    member = "Finalize";
                    break;
                case OperatorDeclarationSyntax @operator when member == null:
                    member = "op_" + @operator.OperatorToken.ValueText;
                    break;
                case ConversionOperatorDeclarationSyntax when member == null:
                    member = "op_Conversion";
                    break;
                case AccessorDeclarationSyntax accessor when member == null:
                    member = accessor.Parent?.Parent switch
                    {
                        PropertyDeclarationSyntax property => property.Identifier.ValueText,
                        EventDeclarationSyntax @event => @event.Identifier.ValueText,
                        IndexerDeclarationSyntax => "this",
                        _ => "accessor",
                    };
                    break;
            }
        }

        types.Reverse();
        var owner = types.Count == 0 ? "Global" : string.Join(".", types.Select(Sanitize));
        if (member != null)
        {
            owner += "." + Sanitize(member);
        }

        return owner;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "_";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '.' ? ch : '_');
        }

        // Keep the manifest resource name readable and well below filesystem
        // limits; the content hash already guarantees uniqueness.
        return builder.Length <= 80 ? builder.ToString() : builder.ToString(0, 80);
    }
}
