using CSharpToJava.Core.AsyncNormalize;
using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// C38 Java-half conversion gate. The C18 async-normalize pass expands
/// <c>await foreach</c>/<c>await using</c> on the C# side; these tests feed
/// that EXPANDED shape (GetAsyncEnumerator + while(await MoveNextAsync()) +
/// Current + finally DisposeAsync) through the main conversion pipeline and
/// pin the Java half: the <c>io.github.ningpp.compat</c> async-streams bridge
/// types/methods, <c>CompletableFuture.join()</c> blocking, Boolean unboxing
/// in the while condition, and zero <c>configureAwait</c>/C# residue
/// (C38 design §5, C18 design §1.2 item 2).
/// </summary>
public class AsyncStreamsConversionTests : ConversionTestBase
{
    // Expanded await foreach: exactly the shape AsyncForeachRewriter emits with
    // the default plain try/finally (FaithfulAsyncDisposeGuard=false). The bare
    // source site renders the global:: FQF token (exact preprocessor output);
    // the WithCancellation(CancellationToken.None) forwarding path keeps the
    // simple-name token expression verbatim (second method).
    private const string ExpandedAwaitForeachConsumer = @"
using System;
using System.Threading;
using System.Threading.Tasks;

class C
{
    public async Task ConsumeBare(System.Collections.Generic.IAsyncEnumerable<int> src)
    {
        var __cs2jAsE_0 = src.GetAsyncEnumerator(global::System.Threading.CancellationToken.None);
        try
        {
            while (await __cs2jAsE_0.MoveNextAsync())
            {
                var x = __cs2jAsE_0.Current;
                Console.WriteLine(x);
            }
        }
        finally
        {
            await __cs2jAsE_0.DisposeAsync();
        }
    }

    public async Task ConsumeNoneToken(System.Collections.Generic.IAsyncEnumerable<int> src)
    {
        var __cs2jAsE_0 = src.GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (await __cs2jAsE_0.MoveNextAsync())
            {
                var x = __cs2jAsE_0.Current;
                Console.WriteLine(x);
            }
        }
        finally
        {
            await __cs2jAsE_0.DisposeAsync();
        }
    }
}
";

    // await using equivalent: ordinary try/finally + await DisposeAsync().
    private const string ExpandedAwaitUsingConsumer = @"
using System;
using System.Threading.Tasks;

class C
{
    public async Task Use(System.IAsyncDisposable resource)
    {
        try
        {
            Console.WriteLine(1);
        }
        finally
        {
            await resource.DisposeAsync();
        }
    }
}
";

    [Fact]
    public void ExpandedAwaitForeach_MapsToBridgeTypesAndJoinBlocking()
    {
        var result = Convert(ExpandedAwaitForeachConsumer);

        AssertSuccess(result);
        AssertNoCSharpResidue(result);

        // Bridge type names and imports.
        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncEnumerable;");
        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncEnumerator;");
        AssertJavaContains(result, "AsyncEnumerable<Integer> src");

        // Four mapped bridge calls + blocking joins.
        AssertJavaContains(result, ".getAsyncEnumerator(");
        // CancellationToken.None → getNone(): the simple-name forwarding path
        // renders clean; the bare-site global:: FQF keeps the converter's
        // general FQF-receiver prefix (System.Threading.CancellationToken,
        // same pre-existing behavior as System.Console) but the mapped member
        // name still lands on getNone() in both forms.
        AssertJavaContains(result, ".getNone()");
        AssertJavaContains(result, ".moveNextAsync())");
        AssertJavaContains(result, ".getCurrent()");
        AssertJavaContains(result, ".disposeAsync())");

        // While condition: the boxed Boolean result of AsyncAwait.join(...) is
        // unboxed to primitive boolean by the converter's join-unbox table.
        AssertJavaContains(result, "while (");
        Assert.True(
            result.GeneratedCode.Contains("(boolean)") && result.GeneratedCode.Contains("AsyncAwait.join("),
            "while condition must unbox the boxed Boolean AsyncAwait.join result to primitive boolean.\n"
            + "--- Generated ---\n" + result.GeneratedCode);

        // No C# surface names survive.
        AssertJavaDoesNotContain(result, "IAsyncEnumerable");
        AssertJavaDoesNotContain(result, "IAsyncEnumerator");
        AssertJavaDoesNotContain(result, "GetAsyncEnumerator");
        AssertJavaDoesNotContain(result, "MoveNextAsync");
        AssertJavaDoesNotContain(result, "DisposeAsync");
        // ConfigureAwait is stripped on the C# side; no camelCase leak either.
        AssertJavaDoesNotContain(result, "ConfigureAwait");
        AssertJavaDoesNotContain(result, "configureAwait");
    }

    [Fact]
    public void ExpandedAwaitUsing_MapsDisposeAsyncToJoinStatement()
    {
        var result = Convert(ExpandedAwaitUsingConsumer);

        AssertSuccess(result);
        AssertNoCSharpResidue(result);

        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncDisposable;");
        AssertJavaContains(result, "AsyncDisposable resource");
        AssertJavaContains(result, "finally {");
        AssertJavaContains(result, "AsyncAwait.join(resource.disposeAsync());");

        AssertJavaDoesNotContain(result, "IAsyncDisposable");
        AssertJavaDoesNotContain(result, "DisposeAsync");
        AssertJavaDoesNotContain(result, "configureAwait");
    }

    [SlowE2EFact(6.9)]
    public void AwaitForeachSource_PreprocessedThenConverted_EndToEndBridgeText()
    {
        const string source = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

class HandmadeEnumerable : IAsyncEnumerable<int>
{
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new HandmadeEnumerator();
}

class HandmadeEnumerator : IAsyncEnumerator<int>
{
    private int _current;
    public int Current => _current;
    public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(++_current <= 3);
    public ValueTask DisposeAsync() => default;
}

class C
{
    public async Task Consume()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
}
";

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "async-streams-e2e",
            new[] { tree },
            BuildTrustedReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessed = new AsyncNormalizePreprocessor().Preprocess(
            source, compilation.GetSemanticModel(tree), new AsyncNormalizeOptions());

        Assert.True(preprocessed.Changed,
            "await foreach must expand via AsyncNormalizePreprocessor\n" +
            string.Join("\n", preprocessed.Diagnostics));
        Assert.Equal(1, preprocessed.Statistics.AwaitForeachExpanded);
        Assert.Contains("GetAsyncEnumerator(", preprocessed.OutputCode);
        Assert.Contains("MoveNextAsync()", preprocessed.OutputCode);
        Assert.Contains("DisposeAsync()", preprocessed.OutputCode);
        // Default = plain try/finally (net10 truth), no faithful-guard flag.
        Assert.DoesNotContain("__cs2jAsOk_", preprocessed.OutputCode);

        var result = Convert(preprocessed.OutputCode);

        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, ".getAsyncEnumerator(");
        AssertJavaContains(result, ".moveNextAsync())");
        AssertJavaContains(result, ".getCurrent()");
        AssertJavaContains(result, ".disposeAsync())");
        AssertJavaContains(result, "AsyncEnumerator<Integer>");
        AssertJavaDoesNotContain(result, "ConfigureAwait");
        AssertJavaDoesNotContain(result, "configureAwait");
        AssertJavaDoesNotContain(result, "await foreach");
    }

    [Fact]
    public void AsyncIteratorProducer_PreprocessedThenConverted_LoweredStateMachineMapsToBridge()
    {
        const string source = @"
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

class C
{
    public async IAsyncEnumerable<int> Mk(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1, cancellationToken);
            yield return i;
        }
    }

    public async Task Consume()
    {
        var e = Mk().GetAsyncEnumerator(global::System.Threading.CancellationToken.None);
        try
        {
            while (await e.MoveNextAsync())
            {
                Console.WriteLine(e.Current);
            }
        }
        finally
        {
            await e.DisposeAsync();
        }
    }
}
";

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "async-iterator-e2e",
            new[] { tree },
            BuildTrustedReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessed = new AsyncNormalizePreprocessor().Preprocess(
            source, compilation.GetSemanticModel(tree), new AsyncNormalizeOptions());

        Assert.True(preprocessed.Changed,
            "async iterator must lower via AsyncNormalizePreprocessor\n" +
            string.Join("\n", preprocessed.Diagnostics));
        Assert.Equal(1, preprocessed.Statistics.AsyncIteratorsLowered);
        Assert.Equal(2, preprocessed.Statistics.AsyncIteratorClassesEmitted);
        Assert.Contains("Cs2jAsyncIterator_", preprocessed.OutputCode);
        Assert.Contains("Cs2jAsyncEnumerable_", preprocessed.OutputCode);
        Assert.DoesNotContain("yield return", preprocessed.OutputCode);

        // The lowered state machine dispatches via goto/label; the standard
        // CLI preprocessing chain therefore runs GotoEliminator before the
        // Java conversion — do the same here, and require zero skips.
        var eliminated = new GotoEliminator().Eliminate(preprocessed.OutputCode);
        Assert.True(eliminated.Changed,
            "the lowered state machine contains goto/label dispatch to eliminate");
        Assert.Empty(eliminated.Diagnostics);

        var result = Convert(eliminated.OutputCode);

        AssertSuccess(result);
        AssertNoCSharpResidue(result);

        // Bridge type imports...
        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncEnumerable;");
        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncEnumerator;");
        AssertJavaContains(result, "import io.github.ningpp.compat.AsyncDisposable;");

        // ...factory + state-machine driving surface.
        AssertJavaContains(result, ".getAsyncEnumerator(");
        AssertJavaContains(result, ".moveNextAsync()");
        AssertJavaContains(result, ".getCurrent()");
        AssertJavaContains(result, ".disposeAsync()");
        AssertJavaContains(result, ".moveNextAsync())");
        AssertJavaContains(result, ".disposeAsync())");
        AssertJavaContains(result, ".getNone()");

        AssertJavaDoesNotContain(result, "yield");
        AssertJavaDoesNotContain(result, "ConfigureAwait");
        AssertJavaDoesNotContain(result, "configureAwait");
    }

    private static IReadOnlyList<MetadataReference> BuildTrustedReferences()
    {
        var result = new List<MetadataReference>();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    result.Add(MetadataReference.CreateFromFile(assembly));
            }
        }
        else
        {
            result.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return result;
    }
}
