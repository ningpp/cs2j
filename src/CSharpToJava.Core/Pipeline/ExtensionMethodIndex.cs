using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Describes a single extension method discovered during pre-conversion scanning.
/// Keyed by <see cref="DocumentationCommentId"/> for stable cross-compilation lookup.
/// </summary>
public sealed class ExtensionMethodDescriptor
{
    /// <summary>
    /// Stable cross-compilation identifier (Roslyn documentation comment ID, e.g. "M:Ns.Extensions.Foo(...)").
    /// </summary>
    public required string DocumentationCommentId { get; init; }

    /// <summary>
    /// Name of the project that declares this extension method.
    /// </summary>
    public required string DeclaringProjectName { get; init; }

    /// <summary>
    /// Fully-qualified C# type name of the static host class (e.g. "MyNamespace.MyExtensions").
    /// </summary>
    public required string DeclaringTypeFullName { get; init; }

    /// <summary>
    /// The method name as declared in C#.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Fully-qualified C# type name of the receiver (first 'this' parameter type).
    /// </summary>
    public required string ReceiverTypeFullName { get; init; }

    /// <summary>
    /// Number of generic type parameters on the method.
    /// </summary>
    public int GenericArity { get; init; }

    /// <summary>
    /// Java package derived from the declaring namespace.
    /// </summary>
    public required string JavaPackage { get; init; }

    /// <summary>
    /// Java host class name (simple name, e.g. "MyExtensions").
    /// </summary>
    public required string JavaHostClassName { get; init; }

    /// <summary>
    /// Java method name (camelCase of the C# name).
    /// </summary>
    public required string JavaMethodName { get; init; }

    /// <summary>
    /// Fully-qualified Java host class (package + class name).
    /// </summary>
    public string JavaHostClassFullName =>
        string.IsNullOrEmpty(JavaPackage) ? JavaHostClassName : $"{JavaPackage}.{JavaHostClassName}";
}

/// <summary>
/// Index of all extension methods discovered across all projects in the current conversion graph.
/// Built once before per-project conversion begins; consumed by transformers and validation passes.
/// Thread-safe for concurrent reads after construction.
/// </summary>
public sealed class ExtensionMethodIndex
{
    private readonly Dictionary<string, ExtensionMethodDescriptor> _byDocCommentId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionMethodDescriptor>> _byReceiverType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionMethodDescriptor>> _byProject = new(StringComparer.Ordinal);
    private readonly bool _isReadOnly;

    public static ExtensionMethodIndex Empty { get; } = new(isReadOnly: true);

    public ExtensionMethodIndex() : this(isReadOnly: false) { }

    private ExtensionMethodIndex(bool isReadOnly)
    {
        _isReadOnly = isReadOnly;
    }

    public int Count => _byDocCommentId.Count;

    public void Add(ExtensionMethodDescriptor descriptor)
    {
        if (_isReadOnly)
            throw new InvalidOperationException("Cannot modify a read-only ExtensionMethodIndex.");

        _byDocCommentId[descriptor.DocumentationCommentId] = descriptor;

        if (!_byReceiverType.TryGetValue(descriptor.ReceiverTypeFullName, out var receiverList))
        {
            receiverList = [];
            _byReceiverType[descriptor.ReceiverTypeFullName] = receiverList;
        }
        receiverList.Add(descriptor);

        if (!_byProject.TryGetValue(descriptor.DeclaringProjectName, out var projectList))
        {
            projectList = [];
            _byProject[descriptor.DeclaringProjectName] = projectList;
        }
        projectList.Add(descriptor);
    }

    /// <summary>
    /// Look up an extension method by its Roslyn documentation comment ID.
    /// </summary>
    public ExtensionMethodDescriptor? FindByDocCommentId(string docCommentId)
        => _byDocCommentId.GetValueOrDefault(docCommentId);

    /// <summary>
    /// Find all extension methods targeting the given receiver type.
    /// </summary>
    public IReadOnlyList<ExtensionMethodDescriptor> FindByReceiverType(string receiverTypeFullName)
        => _byReceiverType.GetValueOrDefault(receiverTypeFullName) ?? (IReadOnlyList<ExtensionMethodDescriptor>)[];

    /// <summary>
    /// Find all extension methods declared in the given project.
    /// </summary>
    public IReadOnlyList<ExtensionMethodDescriptor> FindByProject(string projectName)
        => _byProject.GetValueOrDefault(projectName) ?? (IReadOnlyList<ExtensionMethodDescriptor>)[];

    /// <summary>
    /// All descriptors in the index.
    /// </summary>
    public IEnumerable<ExtensionMethodDescriptor> All => _byDocCommentId.Values;

    /// <summary>
    /// Scans a Roslyn compilation for extension methods and adds them to this index.
    /// </summary>
    public static void ScanCompilation(
        ExtensionMethodIndex index,
        CSharpCompilation compilation,
        string projectName,
        Func<string, string> namespaceToPackage)
    {
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrWhiteSpace(syntaxTree.FilePath)
                || syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                // Extension methods must have 'this' on the first parameter
                if (methodDecl.ParameterList?.Parameters.Count is null or 0)
                    continue;
                var firstParam = methodDecl.ParameterList.Parameters[0];
                if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
                    continue;

                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol is not { IsExtensionMethod: true, IsStatic: true })
                    continue;

                var containingType = methodSymbol.ContainingType;
                if (containingType == null)
                    continue;

                var docId = methodSymbol.GetDocumentationCommentId();
                if (string.IsNullOrEmpty(docId))
                    continue;

                var receiverType = methodSymbol.Parameters[0].Type;
                // Use OriginalDefinition for generic types so List<T> is stored
                // instead of List<string>, enabling receiver-type lookups that are
                // consistent regardless of call-site type arguments.
                var receiverTypeForIndex = receiverType.OriginalDefinition ?? receiverType;
                var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                if (ns == "<global namespace>") ns = string.Empty;
                var javaPackage = namespaceToPackage(ns);
                var javaMethodName = methodSymbol.Name.Length > 0
                    ? char.ToLowerInvariant(methodSymbol.Name[0]) + methodSymbol.Name[1..]
                    : methodSymbol.Name;

                index.Add(new ExtensionMethodDescriptor
                {
                    DocumentationCommentId = docId,
                    DeclaringProjectName = projectName,
                    DeclaringTypeFullName = containingType.ToDisplayString(),
                    MethodName = methodSymbol.Name,
                    ReceiverTypeFullName = receiverTypeForIndex.ToDisplayString(),
                    GenericArity = methodSymbol.TypeParameters.Length,
                    JavaPackage = javaPackage,
                    JavaHostClassName = containingType.Name,
                    JavaMethodName = javaMethodName,
                });
            }
        }
    }
}
