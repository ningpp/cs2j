using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Task.Factory.StartNew(...) must map to the compat Task class's static
/// getFactory() (io.github.ningpp.compat.Task), not CompletableFuture.getFactory().
/// The System.Threading.Tasks.Task type maps to CompletableFuture for async/await
/// usage, but Task.Factory is only implemented on the compat Task class.
/// Regression: GraphmapsWpfControl TileFetcher.UpdateTiles.
/// </summary>
public class _PlaceholderTaskFactoryCompatTests
{
}
