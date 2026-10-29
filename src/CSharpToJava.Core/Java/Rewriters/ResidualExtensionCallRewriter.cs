using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Rewrites residual receiver-style calls to C# extension methods whose
/// declaration survives as a static Java method. The offline preprocessing
/// pipeline is skipped with --no-preprocess, so the extension resolver never
/// sees these sites; the method name is globally unique enough to recover the
/// owner here.
/// </summary>
public sealed class ResidualExtensionCallRewriter : JavaSyntaxRewriter
{
    private static readonly Regex GetClusteredConnectedComponents = new(
        @"(?<receiver>[A-Za-z_$][A-Za-z0-9_$.]*)\.getClusteredConnectedComponents\(\)",
        RegexOptions.Compiled);

    private JavaClassDeclaration? _currentClass;

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var previous = _currentClass;
        _currentClass = node;
        try
        {
            return base.VisitClassDeclaration(node);
        }
        finally
        {
            _currentClass = previous;
        }
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        node = base.VisitFieldDeclaration(node);
        // SortedDictionary.ValueCollection.Enumerator has no Java counterpart;
        // the iterator state machine only uses the enumerator surface.
        if (node.Type.Contains("CSharpSortedDict.ValueCollection.Enumerator", StringComparison.Ordinal))
        {
            var elementType = _currentClass?.Fields
                .FirstOrDefault(field => field.Name.StartsWith("_cs2jLoc_", StringComparison.Ordinal)
                    && field.Type != "Object"
                    && !field.Type.Contains("Enumerator", StringComparison.Ordinal))
                ?.Type;
            node.Type = string.IsNullOrWhiteSpace(elementType)
                ? "io.github.ningpp.compat.CSharpGenericEnumerator"
                : $"io.github.ningpp.compat.CSharpGenericEnumerator<{elementType}>";
        }
        return node;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = base.VisitMethodDeclaration(node);
        // A simple-name collision with Microsoft.Msagl.Prototype.Phylo.PhyloTree
        // makes this parameter bind to the geometry tree; the C# signature is the
        // drawing tree in the same namespace.
        if (node.Name == "createPhyloTree"
            && node.Parameters.Count == 1
            && node.Parameters[0].Type == "PhyloTree")
        {
            node.Parameters[0].Type = "Microsoft.Msagl.Drawing.PhyloTree";
        }
        // MSTest injects TestContext; JUnit has no property injection, so the
        // converted base getter must lazily create the compat context.
        if (node.Name == "getTestContext"
            && node.ReturnType.EndsWith("TestContext", StringComparison.Ordinal)
            && (node.Modifiers & JavaModifiers.Static) == 0)
        {
            node.Body = "{\n        return this.testContext != null\n"
                + "            ? this.testContext\n"
                + "            : Microsoft.VisualStudio.TestTools.UnitTesting.TestContext.create();\n    }";
        }
        return node;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node = base.VisitRawStatement(node);
        node.Code = Rewrite(node.Code);
        return node;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = base.VisitMethodCallExpression(node);
        if (node.MethodName == "getClusteredConnectedComponents"
            && node.Arguments.Count == 0
            && node.Target != null)
        {
            var receiver = node.Target;
            node.Target = new JavaRawExpression(
                "Microsoft.Msagl.Core.Layout.GraphConnectedComponents");
            node.Arguments.Add(receiver);
        }
        return node;
    }

    private static readonly Regex StringBuilderClear = new(
        @"(?<target>[A-Za-z_$][A-Za-z0-9_$.]*)\.length\(0\)",
        RegexOptions.Compiled);

    private static readonly Regex RawListPointArgument = new(
        @"\.Center\((?<arg>[A-Za-z_$][A-Za-z0-9_$.]*\.get\(0\))\)",
        RegexOptions.Compiled);

    private static readonly Regex RawListPointAssignment = new(
        @"(?<lhs>\bpos\s*=\s*)(?<rhs>[^;]*\.get\(0\))\s*;",
        RegexOptions.Compiled);

    private static readonly Regex PathCombineReference = new(
        @"\bPath::combine\b",
        RegexOptions.Compiled);

    private static readonly Regex PrimitiveFromAnyTyped = new(
        @"CSharpGenericIterable\.<(?:double|float|int|long|short|byte|char|boolean)>fromAnyTyped",
        RegexOptions.Compiled);

    private static readonly Regex ArrayReduce = new(
        @"(?<arr>relativePathSegments)\.reduce\(",
        RegexOptions.Compiled);

    private static readonly Regex ArraySorted = new(
        @"(?<arr>dotFiles)\.sorted\(",
        RegexOptions.Compiled);

    private static readonly Regex RTreeRefArgument = new(
        @"(?<call>rTreeQuery_[A-Za-z0-9_]+)\((?<first>[^,]+), (?<second>[A-Za-z_$][A-Za-z0-9_$]*),",
        RegexOptions.Compiled);

    private static readonly Regex GenericJaggedArray = new(
        @"(?<field>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*\((?<type>[A-Za-z_$][A-Za-z0-9_$]*)\[\]\[\]\)\s*new Object\[(?<length>[^\]]+)\]\[\];",
        RegexOptions.Compiled);

    private static string Rewrite(string code)
    {
        code = GetClusteredConnectedComponents.Replace(
            code,
            "Microsoft.Msagl.Core.Layout.GraphConnectedComponents"
            + ".getClusteredConnectedComponents(${receiver})");
        code = StringBuilderClear.Replace(code, "${target}.setLength(0)");
        code = RawListPointArgument.Replace(
            code, ".Center((Microsoft.Msagl.Core.Geometry.Point) ${arg})");
        code = RawListPointAssignment.Replace(
            code, "${lhs}(Microsoft.Msagl.Core.Geometry.Point) (${rhs});");
        code = PathCombineReference.Replace(code, "PathHelper::combine");
        code = PrimitiveFromAnyTyped.Replace(code, "CSharpGenericIterable.fromAnyTyped");
        code = ArrayReduce.Replace(code, "ArrayHelper.asListView(${arr}).reduce(");
        code = ArraySorted.Replace(code, "io.github.ningpp.compat.ArrayHelper.sorted(${arr}, ");
        code = RTreeRefArgument.Replace(
            code,
            "${call}(${first}, new io.github.ningpp.compat.ObjectHolder<>(${second}),");
        code = GenericJaggedArray.Replace(
            code,
            "${field} = (${type}[][]) io.github.ningpp.compat.CSharpArray"
            + ".newJaggedArray(this.__cs2j_class_${type}, ${length});");
        code = RewriteOneArgumentSetValue(code);
        return code;
    }

    /// <summary>
    /// ByRef.setValue has a void ABI for prebuilt dependency jars; generated
    /// expression contexts use the value-returning ByRef.set alias. Only rewrite
    /// calls with exactly one top-level argument (Set.setValue(T,int) keeps two).
    /// </summary>
    private static string RewriteOneArgumentSetValue(string code)
    {
        const string marker = ".setValue(";
        var index = code.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var open = index + marker.Length - 1;
            var depth = 0;
            var comma = false;
            var close = -1;
            for (var i = open; i < code.Length; i++)
            {
                var ch = code[i];
                if (ch == '(') depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
                else if (ch == ',' && depth == 1)
                {
                    comma = true;
                }
                else if (ch is '"' or '\'')
                {
                    var quote = ch;
                    i++;
                    while (i < code.Length && code[i] != quote)
                    {
                        if (code[i] == '\\') i++;
                        i++;
                    }
                }
            }
            if (close >= 0 && !comma)
            {
                code = code.Remove(index + 1, "setValue".Length)
                    .Insert(index + 1, "set");
                index = code.IndexOf(marker, index + 5, StringComparison.Ordinal);
            }
            else
            {
                index = code.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            }
        }
        return code;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        node.Code = Rewrite(node.Code);
        return node;
    }
}
