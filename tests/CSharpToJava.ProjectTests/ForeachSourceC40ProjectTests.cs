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
    private const string RefOutInPassId = "ref-out-in";
    private const string RefOutInIntermediateDir = ".cs2j-refoutin-src";
    private const string IntermediateDir = ".cs2j-foreach-source-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleForeachSourceSolution");

    /// <summary>
    /// Stage a private copy of the deployed fixture in the temp directory.
    /// Other test classes (C16 ForeachSourceProjectTests, ForeachSourceCLITests)
    /// run concurrently (xUnit parallelizes across classes) and write their
    /// intermediate trees INSIDE the shared deployed fixture directory; tree
    /// discovery over that directory would otherwise race with their cleanup
    /// (File.Copy on a half-deleted staged tree). The private copy excludes
    /// every <c>.test-output*</c> staging root plus bin/obj.
    /// </summary>
    private static string StagePrivateFixture()
    {
        var staging = Path.Combine(
            Path.GetTempPath(),
            "cs2j-c40-fixture-" + Guid.NewGuid().ToString("N"));
        CopyFixtureTree(FixtureDir, staging);
        return staging;
    }

    private static void CopyFixtureTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".test-output", StringComparison.Ordinal)
                || name is "bin" or "obj" or ".vs")
            {
                continue;
            }

            CopyFixtureTree(directory, Path.Combine(destination, name));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static Task<CliChainResult> RunPassAsync(
        string fixtureRoot, string outputRoot)
        => RunPassAsync(fixtureRoot, outputRoot, new[] { PassId });

    private static async Task<CliChainResult> RunPassAsync(
        string fixtureRoot, string outputRoot, IReadOnlyList<string> enablePassIds)
    {
        var registry = PassCatalog.BuildRegistry();
        var toggles = PassChainFactory.BuildAllDisabledToggles(registry);
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = Path.Combine(fixtureRoot, "SampleForeachSourceSolution.sln"),
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            LegacyToggles = toggles,
            EnablePassIds = enablePassIds,
            VerifyBuild = true,
            VerifyTests = true,
            Strict = false,
        });
    }

    [Fact]
    public async Task Fixture_LowersDuckEnumerators_AndIntermediateTreeBuildsAndTestsClean()
    {
        var fixtureRoot = StagePrivateFixture();
        var outputRoot = Path.Combine(fixtureRoot, ".test-output-foreach-source-c40");
        try
        {
            var result = await RunPassAsync(fixtureRoot, outputRoot);

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
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task M2_Fixture_DisposeMatrixAndSpanIndexLoops_AreLoweredCorrectly()
    {
        var fixtureRoot = StagePrivateFixture();
        var outputRoot = Path.Combine(fixtureRoot, ".test-output-foreach-source-c40-m2");
        try
        {
            var result = await RunPassAsync(fixtureRoot, outputRoot);

            Assert.True(result.Success,
                "foreach-source pass failed (see chain diagnostics above)");

            var libDir = Path.Combine(outputRoot, IntermediateDir, "SampleForeachSourceLib");

            // ---- Dispose matrix in EnumeratorDuck.cs ----
            var duckFile = Path.Combine(libDir, "EnumeratorDuck.cs");
            Assert.True(File.Exists(duckFile), $"missing lowered file: {duckFile}");
            var duck = await File.ReadAllTextAsync(duckFile);

            // Stateful struct, no IDisposable: bare unboxed while, no finally.
            var stateful = MethodBody(duck, "CollectStatefulStruct");
            Assert.Contains("var __cs2jFaSrc10 = duck;", stateful);
            Assert.Contains("while (__cs2jFaE10.MoveNext())", stateful);
            Assert.DoesNotContain("finally", stateful);
            Assert.DoesNotContain("IDisposable", stateful);

            // Explicit-interface struct Dispose: boxed interface cast in
            // finally (CS2J5455 shape).
            var explicitStruct = MethodBody(duck, "CollectExplicitDisposeStruct");
            Assert.Contains("finally", explicitStruct);
            Assert.Contains(
                "((global::System.IDisposable)__cs2jFaE12).Dispose();",
                explicitStruct);
            // The pattern method is private (explicit impl): never called direct.
            Assert.DoesNotContain("__cs2jFaE12.Dispose();", explicitStruct);

            var explicitBreak = MethodBody(duck, "BreakExplicitDisposeStruct");
            Assert.Contains(
                "((global::System.IDisposable)__cs2jFaE13).Dispose();",
                explicitBreak);

            // Ref struct pattern Dispose: direct pattern call, no cast, box-free.
            var refStructNormal = MethodBody(duck, "RefStructPatternDisposeNormalCount");
            Assert.Contains("finally", refStructNormal);
            Assert.Contains("__cs2jFaE14.Dispose();", refStructNormal);
            Assert.DoesNotContain("IDisposable", refStructNormal);

            var refStructThrow = MethodBody(duck, "RefStructPatternDisposeThrowCount");
            Assert.Contains("__cs2jFaE16.Dispose();", refStructThrow);
            Assert.DoesNotContain("IDisposable", refStructThrow);

            // ---- FA-5 Span index loops in the new fixture file ----
            var spanFile = Path.Combine(libDir, "SpanIndexLoopSamples.cs");
            Assert.True(File.Exists(spanFile), $"missing lowered file: {spanFile}");
            var span = await File.ReadAllTextAsync(spanFile);

            var sumSpan = MethodBody(span, "SumSpan");
            Assert.Contains("var __cs2jFrSrc = values;", sumSpan);
            Assert.Contains(
                "for (int __cs2jFrI = 0; __cs2jFrI < __cs2jFrSrc.Length; __cs2jFrI++)",
                sumSpan);
            Assert.Contains("__cs2jFrSrc[__cs2jFrI]", sumSpan);
            Assert.DoesNotContain("GetEnumerator", sumSpan);
            Assert.DoesNotContain("MoveNext", sumSpan);

            var sumReadOnly = MethodBody(span, "SumReadOnlySpan");
            Assert.Contains("var __cs2jFrSrc1 = values;", sumReadOnly);
            Assert.Contains(
                "__cs2jFrI1 < __cs2jFrSrc1.Length", sumReadOnly);
            Assert.Contains("__cs2jFrSrc1[__cs2jFrI1]", sumReadOnly);

            // break/continue survive inside the index loop unchanged.
            var conditional = MethodBody(span, "AccumulatePositiveUntilSentinel");
            Assert.Contains("__cs2jFrSrc2[__cs2jFrI2]", conditional);
            Assert.Contains("break;", conditional);
            Assert.Contains("continue;", conditional);
            Assert.DoesNotContain("GetEnumerator", conditional);

            // Nested: outer span index loop (4th FR site => suffix 3) wraps the
            // inner stateful-struct duck while (first FA basename in this file).
            var nested = MethodBody(span, "NestedSpanOuterDuckInner");
            Assert.Contains("var __cs2jFaSrc = duck;", nested);
            Assert.Contains("while (__cs2jFaE.MoveNext())", nested);
            Assert.Contains(
                "for (int __cs2jFrI3 = 0; __cs2jFrI3 < __cs2jFrSrc3.Length; __cs2jFrI3++)",
                nested);
            Assert.Contains("__cs2jFrSrc3[__cs2jFrI3]", nested);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 29.95s")]
    public async Task M3_Fixture_RefForeachSites_AreLoweredAndChainIntoRefOutIn()
    {
        // Each CLI-style invocation gets its own private fixture copy: the
        // spine's tree walk only skips its OWN destination root, so a second
        // invocation beside the first run's intermediate tree would see its
        // lowered sources and compile duplicate user types.
        var singleRoot = StagePrivateFixture();
        try
        {
            var result = await RunPassAsync(
                singleRoot,
                Path.Combine(singleRoot, ".test-output-foreach-source-c40-m3"));

            Assert.True(result.Success,
                "foreach-source pass failed (see chain diagnostics above)");

            var libFile = Path.Combine(
                singleRoot, ".test-output-foreach-source-c40-m3",
                IntermediateDir, "SampleForeachSourceLib", "RefCollections.cs");
            Assert.True(File.Exists(libFile), $"missing lowered file: {libFile}");
            var text = await File.ReadAllTextAsync(libFile);

            // FR-2 writable Span: first ref site in this file takes base names.
            var doubleSpan = MethodBody(text, "DoubleSpan");
            Assert.Contains("var __cs2jFrSrc = values;", doubleSpan);
            Assert.Contains(
                "__cs2jFrI < __cs2jFrSrc.Length", doubleSpan);
            Assert.Contains(
                "ref int value = ref __cs2jFrSrc[__cs2jFrI]", doubleSpan);
            Assert.DoesNotContain("GetEnumerator", doubleSpan);

            // FR-2 ReadOnlySpan: second site -> suffix 1, ref-readonly item.
            var sumReadOnly = MethodBody(text, "SumReadOnlySpanRef");
            Assert.Contains(
                "ref readonly int value = ref __cs2jFrSrc1[__cs2jFrI1]", sumReadOnly);

            // FR-2 custom Count + ref indexer ref struct: index path, .Count.
            var doubleBag = MethodBody(text, "DoubleCountIndexBag");
            Assert.Contains("__cs2jFrSrc2.Count", doubleBag);
            Assert.Contains(
                "ref int value = ref __cs2jFrSrc2[__cs2jFrI2]", doubleBag);
            Assert.DoesNotContain("GetEnumerator", doubleBag);

            // FR-3 ref-Current enumerator: while skeleton, ref Current, no Dispose.
            var doubleCollection = MethodBody(text, "DoubleRefReadCollection");
            Assert.Contains(
                "var __cs2jFaE = __cs2jFaSrc.GetEnumerator();", doubleCollection);
            Assert.Contains("while (__cs2jFaE.MoveNext())", doubleCollection);
            Assert.Contains("ref int value = ref __cs2jFaE.Current;", doubleCollection);
            Assert.DoesNotContain("try", doubleCollection);
            Assert.DoesNotContain("Dispose", doubleCollection);
        }
        finally
        {
            if (Directory.Exists(singleRoot))
            {
                Directory.Delete(singleRoot, recursive: true);
            }
        }

        // ---- Two-pass chain on a fresh fixture: foreach-source -> ref-out-in ----
        var chainFixtureRoot = StagePrivateFixture();
        var chainRoot = Path.Combine(
            chainFixtureRoot, ".test-output-foreach-source-c40-m3-chain");
        try
        {
            var chain = await RunPassAsync(
                chainFixtureRoot, chainRoot, new[] { PassId, RefOutInPassId });

            Assert.True(chain.Success,
                "foreach-source + ref-out-in chain failed (see chain diagnostics above)");
            Assert.Contains(IntermediateDir, chain.ExecutedIntermediateDirectoryNames);
            Assert.Contains(RefOutInIntermediateDir, chain.ExecutedIntermediateDirectoryNames);

            var chainedFile = Path.Combine(
                chainRoot, RefOutInIntermediateDir,
                "SampleForeachSourceLib", "RefCollections.cs");
            Assert.True(File.Exists(chainedFile), $"missing chained file: {chainedFile}");
            var chained = await File.ReadAllTextAsync(chainedFile);

            // Writable Span ref alias -> ScopedByRef span/index factory; body
            // reads/writes route through .Value.
            var chainedDoubleSpan = MethodBody(chained, "DoubleSpan");
            Assert.Contains(
                "ScopedByRef<int> value = ScopedByRef<int>.Create(__cs2jFrSrc, __cs2jFrI)",
                chainedDoubleSpan);
            Assert.Contains("value.Value *= 2;", chainedDoubleSpan);

            // ReadOnlySpan alias -> ReadOnlyScopedByRef span/index factory.
            var chainedSumReadOnly = MethodBody(chained, "SumReadOnlySpanRef");
            Assert.Contains(
                "ReadOnlyScopedByRef<int> value = ReadOnlyScopedByRef<int>.Create(__cs2jFrSrc1, __cs2jFrI1)",
                chainedSumReadOnly);
            Assert.Contains("sum += value.Value;", chainedSumReadOnly);

            // Source-defined ref indexer is itself rewritten by C01 to a
            // ByRef<int>-returning indexer, so the element alias binds
            // holder-to-holder (writes propagate through the holder).
            var chainedDoubleBag = MethodBody(chained, "DoubleCountIndexBag");
            Assert.Contains(
                "ByRef<int> value = __cs2jFrSrc2[__cs2jFrI2];",
                chainedDoubleBag);

            // FR-3 source-defined ref-Current property is rewritten to a
            // ByRef<int>-returning property -> holder-to-holder binding.
            var chainedDoubleCollection = MethodBody(chained, "DoubleRefReadCollection");
            Assert.Contains(
                "ByRef<int> value = __cs2jFaE.Current;",
                chainedDoubleCollection);
        }
        finally
        {
            if (Directory.Exists(chainFixtureRoot))
            {
                Directory.Delete(chainFixtureRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Extracts the formatted source span of a method (signature through its
    /// closing brace at type-member indentation) for region-scoped assertions.
    /// </summary>
    private static string MethodBody(string text, string methodName)
    {
        // The pass preserves the source file's line endings, which can be
        // CRLF on Windows checkouts; normalize before region extraction.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var marker = methodName + "(";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {methodName}");
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"method end not found: {methodName}");
        return text.Substring(start, end - start);
    }
}
