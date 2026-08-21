using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Central registry for expression transformers. Transformers self-register via static constructors.
/// </summary>
public static class ExpressionTransformerRegistry
{
    private static readonly ConcurrentDictionary<SyntaxKind, IExpressionTransformer> _transformers = new();

    // Fix 3: Auto-discover all transformers that declare [TransformerRegistration] attribute.
    // This eliminates the brittle hard-coded list of transformer Instance touches and ensures
    // new transformers are discovered automatically without editing this file.
    static ExpressionTransformerRegistry()
    {
        // Trigger static constructors of all transformer types that opt in via attribute
        foreach (var type in typeof(ExpressionTransformerRegistry).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TransformerRegistrationAttribute>() != null))
        {
            var instanceProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            instanceProp?.GetValue(null);
        }

        // Fix 1: Register missing C# 9–12 expression kinds with inline handlers.
        // These kinds have no dedicated transformer class; simple rules are applied inline.
        Register(new[] { SyntaxKind.TupleExpression },
            new DelegateExpressionTransformer((node, ctx) =>
            {
                var tuple = (TupleExpressionSyntax)node;
                var facade = ExpressionTransformerFacade.Instance;
                // Use the semantic model to get the tuple's element types so that
                // implicit C# conversions (e.g. int→double) are applied via
                // AdaptExpressionToTargetType, producing correct Java literals
                // (e.g. 1.0 instead of 1 for a double tuple element).
                ITypeSymbol[]? tupleElementTypes = null;
                if (ctx.SemanticModel != null)
                {
                    var tupleTypeInfo = ctx.GetTypeInfo(node);
                    // Prefer ConvertedType (the target type after implicit C# conversions,
                    // e.g. when (i, i+1, 1, 1, false, 1) is assigned to (int, int, int, int, bool, double))
                    // over Type (the natural type, where "1" is int).
                    var tupleType = tupleTypeInfo.ConvertedType is INamedTypeSymbol { IsTupleType: true } converted
                        ? converted
                        : tupleTypeInfo.Type as INamedTypeSymbol;
                    if (tupleType is { IsTupleType: true })
                    {
                        tupleElementTypes = tupleType.TupleElements
                            .Select(e => e.Type)
                            .ToArray();
                    }
                }
                var elementsList = new System.Collections.Generic.List<string>();
                for (int i = 0; i < tuple.Arguments.Count; i++)
                {
                    var expr = tuple.Arguments[i].Expression;
                    var transformed = facade.Transform(expr, ctx);
                    if (tupleElementTypes != null && i < tupleElementTypes.Length)
                    {
                        transformed = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                            expr, transformed, tupleElementTypes[i], ctx);
                    }
                    elementsList.Add(transformed);
                }
                var elements = string.Join(", ", elementsList);
                ctx.AddImport("io.vavr.Tuple");
                return $"Tuple.of({elements})";
            }));

        Register(new[] { SyntaxKind.DeclarationExpression },
            new DelegateExpressionTransformer((node, ctx) =>
            {
                var decl = (DeclarationExpressionSyntax)node;
                if (decl.Parent is ArgumentSyntax arg
                    && arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                    && decl.Designation is SingleVariableDesignationSyntax svd)
                {
                    var varName = svd.Identifier.Text;
                    var holderName = $"_{varName}Holder";

                    var typeSymbol = ctx.GetTypeInfo(decl.Type).Type
                        ?? ctx.GetTypeInfo(decl).Type;
                    var javaType = typeSymbol != null ? ctx.MapType(typeSymbol) : "Object";
                    if (string.IsNullOrEmpty(javaType))
                        javaType = "Object";

                    var holderType = HolderTypeResolver.GetHolderType(javaType);
                    var holderInit = HolderTypeResolver.GetHolderInstantiation(holderType);

                    ctx.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                    ctx.AddPostStatement($"{javaType} {varName} = {holderName}.value");
                    ctx.SetActiveRefHolder(varName, holderName);
                    return holderName;
                }

                return $"/* TODO: out var {decl.Designation} */";
            }));

        Register(new[] { SyntaxKind.RefExpression },
            new DelegateExpressionTransformer((node, ctx) =>
                ExpressionTransformerFacade.Instance.Transform(((RefExpressionSyntax)node).Expression, ctx)));

        Register(new[] { SyntaxKind.ImplicitStackAllocArrayCreationExpression },
            new DelegateExpressionTransformer((node, ctx) =>
            {
                var stackAlloc = (ImplicitStackAllocArrayCreationExpressionSyntax)node;
                var elements = string.Join(", ", stackAlloc.Initializer.Expressions.Select(e =>
                    ExpressionTransformerFacade.Instance.Transform(e, ctx)));

                // Infer element type from semantic model instead of defaulting to Object[]
                string elementType = "Object";
                var typeInfo = ctx.GetTypeInfo(node);
                if (typeInfo.Type is IArrayTypeSymbol arrType)
                {
                    elementType = ctx.MapType(arrType.ElementType);
                }
                else if (typeInfo.ConvertedType is IArrayTypeSymbol convArr)
                {
                    elementType = ctx.MapType(convArr.ElementType);
                }
                else if (stackAlloc.Initializer.Expressions.Count > 0)
                {
                    var firstTypeInfo = ctx.GetTypeInfo(stackAlloc.Initializer.Expressions[0]);
                    if (firstTypeInfo.Type != null)
                        elementType = ctx.MapType(firstTypeInfo.Type);
                }
                return $"new {elementType}[]{{ {elements} }}";
            }));

        Register(new[] { SyntaxKind.CollectionExpression },
              new DelegateExpressionTransformer((node, ctx) =>
              {
                  var coll = (CollectionExpressionSyntax)node;
                  var elements = string.Join(", ", coll.Elements.OfType<ExpressionElementSyntax>()
                      .Select(e => ExpressionTransformerFacade.Instance.Transform(e.Expression, ctx)));

                  // Check if the target type is an array; if so, generate array initializer.
                  var typeInfo = ctx.GetTypeInfo(node);
                  if (typeInfo.ConvertedType is IArrayTypeSymbol convArr)
                  {
                      var elementType = ctx.MapType(convArr.ElementType);
                      return $"new {elementType}[]{{ {elements} }}";
                  }

                  return $"java.util.List.of({elements})";
              }));

        // global::Namespace.Type used in expression position (e.g. static member
        // receivers like global::dotnet.text.json.JsonCommentHandling.Disallow):
        // map to the Java type name so the member access chain resolves.
        Register(new[] { SyntaxKind.AliasQualifiedName },
            new DelegateExpressionTransformer((node, ctx) =>
            {
                var aqn = (AliasQualifiedNameSyntax)node;
                var typeInfo = ctx.GetTypeInfo(aqn);
                var symbol = typeInfo.Type ?? typeInfo.ConvertedType;
                if (symbol != null && symbol.TypeKind != TypeKind.Error)
                {
                    var mapped = ctx.MapType(symbol);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        return mapped;
                }
                return ctx.MapTypeFromSyntax(aqn.Name) ?? aqn.Name.ToString();
            }));
      }

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
            // Fix 4 (Issue 5): Warn on duplicate registration during debug builds
            if (_transformers.ContainsKey(kind))
                Debug.WriteLine($"[ExpressionTransformerRegistry] WARNING: {kind} already registered; overwriting.");
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

    /// <summary>
    /// Wraps a delegate as an IExpressionTransformer for inline/anonymous registrations.
    /// </summary>
    private sealed class DelegateExpressionTransformer : IExpressionTransformer
    {
        private readonly Func<ExpressionSyntax, ConversionContext, string> _func;
        public DelegateExpressionTransformer(Func<ExpressionSyntax, ConversionContext, string> func) => _func = func;
        public string Transform(ExpressionSyntax node, ConversionContext context) => _func(node, context);
    }
}
