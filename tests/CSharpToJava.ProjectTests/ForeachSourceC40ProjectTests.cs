using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C40 foreach-source M1 (FA-1/FA-2/FA-4 duck
/// enumerator adaptation). Runs the foreach-source pass in isolation over
/// SampleForeachSourceSolution and inspects the lowered intermediate tree:
/// instance/extension duck sites become GetEnumerator + while(MoveNext) +
/// Current skeletons; non-IDisposable enumerators stay bare loops while
/// IDisposable enumerators get try/finally. The fixture's own xUnit tests
/// (EnumeratorDuckTests) run on the rewritten tree and gate runtime
/// equivalence.
/// </summary>
public class ForeachSourceC40ProjectTests
{
    private const string PassId = "foreach-source";
    private const string IntermediateDir = ".cs2j-foreach-source-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleForeachSourceSolution");

    private static async Task<CliChainResult> RunPassAsync(string outputRoot)
    {
        var registry = PassCatalog.BuildRegistry();
        var toggles = PassChainFactory.BuildAllDisabledToggles(registry);
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = Path.Combine(FixtureDir, "SampleForeachSourceSolution.sln"),
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            LegacyToggles = toggles,
            EnablePassIds = new[] { PassId },
            VerifyBuild = true,
            VerifyTests = true,
            Strict = false,
        });
    }

    [Fact]
    public async Task Fixture_LowersDuckEnumerators_AndIntermediateTreeBuildsAndTestsClean()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-foreach-source-c40");
        try
        {
            var result = await RunPassAsync(outputRoot);

            Assert.True(result.Success,
                "foreach-source pass failed (see chain diagnostics above)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var libFile = Path.Combine(
                outputRoot, IntermediateDir, "SampleForeachSourceLib", "EnumeratorDuck.cs");
            Assert.True(File.Exists(libFile), $"missing lowered file: {libFile}");
            var text = await File.ReadAllTextAsync(libFile);

            // FA-1/FA-2 shared skeleton pieces. The fresh-name counter is
            // file-scoped: the first site (CollectPlain) takes the base names.
            Assert.Contains("var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", text);
            Assert.Contains("while (__cs2jFaE.MoveNext())", text);

            // FA-2 keeps instance call syntax; the extension static name is
            // never inlined.
            Assert.DoesNotContain("EnumeratorDuckExtensions.GetEnumerator", text);

            // Non-IDisposable plain duck: bare while, no try/finally.
            var collectPlain = MethodBody(text, "CollectPlain");
            Assert.Contains("var __cs2jFaSrc = duck;", collectPlain);
            Assert.DoesNotContain("try", collectPlain);
            Assert.DoesNotContain("finally", collectPlain);
            Assert.DoesNotContain("Dispose", collectPlain);

            // Source cached once through the factory call (second site ⇒ 1).
            var collectFromFactory = MethodBody(text, "CollectFromFactory");
            Assert.Contains("var __cs2jFaSrc1 = factory.Create();", collectFromFactory);
            Assert.DoesNotContain("finally", collectFromFactory);

            // Extension duck: also a bare loop (no IDisposable), third site ⇒ 2.
            var collectExtension = MethodBody(text, "CollectExtension");
            Assert.Contains("__cs2jFaSrc2.GetEnumerator()", collectExtension);
            Assert.DoesNotContain("finally", collectExtension);

            // IEnumerator<T> return implements IDisposable (fourth site ⇒ 3);
            // the non-generic IEnumerator on net10 does not (fifth site ⇒ 4).
            var genericBoxed = MethodBody(text, "CollectGenericBoxed");
            Assert.Contains("finally", genericBoxed);
            Assert.Contains("__cs2jFaE3.Dispose();", genericBoxed);

            var nonGenericBoxed = MethodBody(text, "CollectNonGenericBoxed");
            Assert.DoesNotContain("finally", nonGenericBoxed);
            // object Current with explicit int element inserts an unbox cast.
            Assert.Contains("(int)__cs2jFaE4.Current", nonGenericBoxed);

            // FA-4: IDisposable enumerator sites carry try/finally + Dispose.
            // NormalCompletion is site 6, ThrowAtSecond site 8 (Break is 7).
            var normalCompletion = MethodBody(text, "NormalCompletionDisposeCount");
            Assert.Contains("try", normalCompletion);
            Assert.Contains("finally", normalCompletion);
            Assert.Contains("__cs2jFaE5.Dispose();", normalCompletion);

            var throwAtSecond = MethodBody(text, "ThrowAtSecond");
            Assert.Contains("finally", throwAtSecond);
            Assert.Contains("__cs2jFaE7.Dispose();", throwAtSecond);

            // The fixture test project is lowered too; its direct duck sites
            // are converted to the while skeleton as well.
            var testsFile = Path.Combine(
                outputRoot, IntermediateDir, "SampleForeachSourceTests", "EnumeratorDuckTests.cs");
            Assert.True(File.Exists(testsFile), $"missing lowered file: {testsFile}");
            var testsText = await File.ReadAllTextAsync(testsFile);
            Assert.Contains("while (__cs2jFaE.MoveNext())", testsText);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Extracts the formatted source span of a method (signature through its
    /// closing brace at type-member indentation) for region-scoped assertions.
    /// </summary>
    private static string MethodBody(string text, string methodName)
    {
        var marker = methodName + "(";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {methodName}");
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"method end not found: {methodName}");
        return text.Substring(start, end - start);
    }
}
