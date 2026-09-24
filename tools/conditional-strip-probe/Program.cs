using CSharpToJava.Core.ConditionalDirectiveStrip;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var cases = new Dictionary<string, string>
{
    ["symbol-equality"] = "#if A == B\nX();\n#endif\n",
    ["invalid-expression"] = "#if A + B\nX();\n#endif\n",
    ["inactive-warning"] = "#if A\n#warning hidden\n#endif\n",
    ["inactive-error"] = "#if A\n#error hidden\n#endif\n",
    ["inactive-pragma"] = "#if A\n#pragma warning disable CS0168\n#endif\n",
    ["inactive-line"] = "#if A\n#line hidden\n#endif\n",
    ["inactive-nullable"] = "#if A\n#nullable enable\n#endif\n",
    ["inactive-region"] = "#if A\n#region R\nx\n#endregion\n#endif\n",
    ["extra-endif"] = "#endif\n",
    ["missing-endif"] = "#if A\nX();\n",
    ["elif-after-else"] = "#if A\n#else\n#elif B\n#endif\n",
    ["define-after-token"] = "class C {}\n#define A\n",
    ["not-first-line"] = "class C { #if A }\n",
    ["bad-directive"] = "#wibble\nclass C {}\n",
    ["reference"] = "#r \"x.dll\"\n",
    ["load"] = "#load \"x.csx\"\n",
    ["shebang"] = "#!/usr/bin/env dotnet\nclass C {}\n",
    ["pragma-checksum"] = "#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"\nclass C {}\n",
};

foreach (var (name, source) in cases)
{
    var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
    Console.WriteLine($"CASE {name}");
    Console.WriteLine("DIAGS " + string.Join(" | ", tree.GetDiagnostics().Select(d => $"{d.Id}:{d.Severity}:{d.GetMessage()}")));
    var root = tree.GetCompilationUnitRoot();
    foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
    {
        if (trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
        {
            var active = trivia.GetStructure() is DirectiveTriviaSyntax directive ? directive.IsActive.ToString() : "-";
            Console.WriteLine($"TRIVIA {trivia.Kind()} active={active} text={Escape(trivia.ToFullString())}");
        }
    }

    var result = new ConditionalDirectiveStripper().Strip(source);
    Console.WriteLine($"STRIP changed={result.Changed} output={Escape(result.OutputCode)} directives={result.Statistics.DirectivesRemoved} disabled={result.Statistics.DisabledBranchesRemoved}");
    Console.WriteLine("STRIPDIAGS " + string.Join(" | ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
}

var outputCases = new Dictionary<string, string>
{
    ["eof-no-newline"] = "class C { }\n#if A\n#endif",
    ["empty-indented"] = "A();\n    #if A\n    #endif\nB();\n",
    ["crlf"] = "#if A\r\nX();\r\n#endif\r\n",
    ["cr"] = "#if A\rX();\r#endif\r",
    ["mixed"] = "#if A\r\nX();\nY();\r#endif\r\n",
    ["stats"] = "#define A\n#if A\nX();\n#else\nY();\n#endif\n",
};
foreach (var (name, source) in outputCases)
{
    var result = new ConditionalDirectiveStripper().Strip(source, new ConditionalDirectiveStripOptions { Symbols = name is "crlf" or "cr" or "mixed" ? ["A"] : [] });
    Console.WriteLine($"OUTPUT {name} changed={result.Changed} value={Escape(result.OutputCode)} directives={result.Statistics.DirectivesRemoved} disabled={result.Statistics.DisabledBranchesRemoved} diags={string.Join('|', result.Diagnostics.Select(d => d.Message))}");
}

static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
