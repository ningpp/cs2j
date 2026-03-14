using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Abstractions;
using System.Collections.Concurrent;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Central registry for expression transformers. Transformers self-register via static constructors.
/// </summary>
public static class ExpressionTransformerRegistry
{
    private static readonly ConcurrentDictionary<SyntaxKind, IExpressionTransformer> _transformers = new();

    /// <summary>
    /// Register a transformer for the specified SyntaxKind values.
    /// Called by transformer static constructors.
    /// </summary>
    /// <param name="kinds">The SyntaxKind values this transformer can handle.</param>
    /// <param name="transformer">The transformer instance.</param>
    public static void Register(SyntaxKind[] kinds, IExpressionTransformer transformer)
    {
        foreach (var kind in kinds)
        {
            _transformers[kind] = transformer;
        }
    }

    /// <summary>
    /// Get the transformer for the given SyntaxKind.
    /// </summary>
    /// <param name="kind">The SyntaxKind to look up.</param>
    /// <returns>The transformer if found, otherwise null.</returns>
    public static IExpressionTransformer? GetTransformer(SyntaxKind kind)
        => _transformers.TryGetValue(kind, out var transformer) ? transformer : null;

    /// <summary>
    /// Check if a SyntaxKind is registered.
    /// </summary>
    /// <param name="kind">The SyntaxKind to check.</param>
    /// <returns>True if a transformer is registered for this kind.</returns>
    public static bool IsRegistered(SyntaxKind kind)
        => _transformers.ContainsKey(kind);
}
