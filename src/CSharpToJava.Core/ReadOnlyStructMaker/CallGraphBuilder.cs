using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Detects cross-method/cross-file mutations of struct fields via ref/out parameters
/// and external public field assignments.
/// </summary>
internal sealed class CallGraphBuilder
{
    private readonly SemanticModel _model;
    private readonly CrossFileAccessIndex? _index;

    public CallGraphBuilder(SemanticModel model, CrossFileAccessIndex? index = null)
    {
        _model = model;
        _index = index;
    }

    /// <summary>
    /// Check if any field of the struct is modified through a ref/out parameter
    /// within the same compilation unit.
    /// </summary>
    public bool IsModifiedThroughRef(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        if (_index != null)
            return _index.IsModifiedThroughRef(root.SyntaxTree, structSymbol.Name);

        var structName = structSymbol.Name;

        // Find all parameters of this struct type with ref/out modifiers
        foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            var hasRefOrOut = param.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword));
            if (!hasRefOrOut) continue;

            // Check if parameter type matches our struct
            var paramSymbol = _model.GetDeclaredSymbol(param);
            if (paramSymbol?.Type is not INamedTypeSymbol paramType) continue;
            if (paramType.Name != structName) continue;

            // Check if any member of this parameter is assigned within the containing method
            var containingMethod = param.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (containingMethod == null) continue;

            var paramName = param.Identifier.Text;
            if (IsParameterFieldModified(containingMethod, paramName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if non-private fields of the struct are accessed by ref/out from outside the
    /// struct definition. Such fields cannot be converted to properties because properties
    /// don't support ref/out access (CS0206).
    /// </summary>
    public bool HasExternalRefFieldAccess(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        if (_index != null)
            return _index.HasExternalRefFieldAccess(structSymbol);

        var accessibleFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.NotApplicable && !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

        if (accessibleFields.Count == 0) return false;

        var structName = structSymbol.Name;
        var compilation = _model.Compilation;

        var rootModel = compilation.GetSemanticModel(root.SyntaxTree);
        if (HasExternalRefAccessInTree(root, accessibleFields, structName, rootModel))
            return true;

        var currentTree = root.SyntaxTree;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree == currentTree) continue;
            var otherRoot = tree.GetRoot();
            var otherModel = compilation.GetSemanticModel(tree);
            if (HasExternalRefAccessInTree(otherRoot, accessibleFields, structName, otherModel))
                return true;
        }

        return false;
    }

    private static bool HasExternalRefAccessInTree(
        SyntaxNode root, HashSet<string> accessibleFields, string structName, SemanticModel model)
    {
        foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;

            // Unwrap ! (SuppressNullableWarningExpression) if present: ref obj.Field! → obj.Field
            var expression = argument.Expression;
            if (expression is PostfixUnaryExpressionSyntax suppress &&
                suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                expression = suppress.Operand;

            if (expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var fieldSymbol = model.GetSymbolInfo(memberAccess).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = argument.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if non-private fields of the struct are assigned from outside the struct definition.
    /// This covers public, internal, protected, and protected-internal fields.
    /// Searches both the current file and all other files in the compilation.
    /// </summary>
    public bool HasExternalPublicFieldAssignment(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        if (_index != null)
            return _index.HasExternalPublicFieldAssignment(structSymbol);

        var accessibleFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.NotApplicable && !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

        if (accessibleFields.Count == 0) return false;

        var structName = structSymbol.Name;
        var compilation = _model.Compilation;

        // Check within the current file - use the semantic model for the correct syntax tree
        var rootModel = compilation.GetSemanticModel(root.SyntaxTree);
        if (HasExternalAssignmentInTree(root, accessibleFields, structName, rootModel))
            return true;

        // Check across other files in the compilation
        var currentTree = root.SyntaxTree;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree == currentTree) continue;
            var otherRoot = tree.GetRoot();
            var otherModel = compilation.GetSemanticModel(tree);
            if (HasExternalAssignmentInTree(otherRoot, accessibleFields, structName, otherModel))
                return true;
        }

        return false;
    }

    private static bool HasExternalAssignmentInTree(
        SyntaxNode root, HashSet<string> accessibleFields, string structName, SemanticModel model)
    {
        // Find object initializers: new StructType { Field = value, ... }
        // These set fields externally and require property conversion for readonly structs.
        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            // Check if this creates the target struct type by comparing type name text
            // Use GetTypeInfo first, then fall back to GetSymbolInfo
            var typeInfo = model.GetTypeInfo(objectCreation.Type);
            string? typeName = typeInfo.Type?.Name;

            // If GetTypeInfo fails, try GetSymbolInfo
            if (typeName == null)
            {
                var symbolInfo = model.GetSymbolInfo(objectCreation.Type);
                typeName = symbolInfo.Symbol?.Name;
            }

            // Final fallback: use the type syntax text (strip generic arguments)
            if (typeName == null)
            {
                typeName = objectCreation.Type.ToString();
                int genericIndex = typeName.IndexOf('<');
                if (genericIndex > 0)
                    typeName = typeName.Substring(0, genericIndex);
            }

            if (typeName != structName)
                continue;

            // Check if there's an initializer
            var initializer = objectCreation.Initializer;
            if (initializer == null)
                continue;

            // Check if any of the initializers set an accessible field
            bool hasFieldAssignment = false;
            foreach (var expr in initializer.Expressions)
            {
                if (expr is AssignmentExpressionSyntax initAssignment &&
                    initAssignment.Left is IdentifierNameSyntax initFieldName &&
                    accessibleFields.Contains(initFieldName.Identifier.Text))
                {
                    hasFieldAssignment = true;
                    break;
                }
            }

            if (hasFieldAssignment)
            {
                // Check if this initializer is outside the struct definition
                var containingStruct = objectCreation.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
                if (containingStruct == null || containingStruct.Identifier.Text != structName)
                {
                    return true;
                }
            }
        }

        // Find assignments to fields of variables typed as this struct, outside the struct itself
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            // Check if this assignment is outside the struct definition
            var containingStruct = assignment.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        // Check ref/out access to accessible fields from outside (e.g. MemoryMarshal.CreateSpan(ref obj.Field, n))
        // Such patterns cannot be converted to properties because properties don't support ref access.
        foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;

            // Unwrap ! (SuppressNullableWarningExpression) if present: ref obj.Field! → obj.Field
            var expression = argument.Expression;
            if (expression is PostfixUnaryExpressionSyntax suppress &&
                suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                expression = suppress.Operand;

            if (expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var fieldSymbol = model.GetSymbolInfo(memberAccess).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = argument.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        // Also check ++/-- on accessible fields from outside
        foreach (var unary in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            var operand = unary.Operand;
            if (operand is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(operand).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = unary.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                continue;
            var operand = unary.Operand;
            if (operand is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(operand).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = unary.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        return false;
    }

    private static bool IsParameterFieldModified(BaseMethodDeclarationSyntax method, string paramName)
    {
        // Check assignments like: param.Field = value
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        // Check ++/-- like: param.Field++
        foreach (var postfix in method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            if (postfix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        foreach (var prefix in method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression &&
                prefix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        // Check compound assignments like: param.Field += value
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression &&
                assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        return false;
    }
}

/// <summary>
/// One-shot, per-compilation index of the cross-file semantic queries that the
/// readonly-struct shaping previously repeated once per candidate struct. The
/// original CallGraphBuilder / StructAnalyzer scans walked every syntax tree and
/// resolved symbols for every node on each <c>Analyze</c> call — an
/// O(structs × trees × nodes) pattern. Building the aggregate result a single
/// time (over the same semantic models, with the identical name/symbol-equality
/// predicates) turns the repeated scans into dictionary lookups and yields the
/// same boolean for every struct.
/// </summary>
internal sealed class CrossFileAccessIndex
{
    // OriginalDefinition of value types written >= 8 times through a ref/out/in
    // parameter declared in a method of another type (frame-size preservation rule).
    private readonly HashSet<ITypeSymbol> _refOutExternalWriteStructs;
    // struct simple name -> field names ref/out-accessed from outside the struct.
    private readonly Dictionary<string, HashSet<string>> _externalRefFields;
    // struct simple name -> field names assigned / ref-accessed / object-initialized
    // from outside the struct.
    private readonly Dictionary<string, HashSet<string>> _externalAssignedFields;
    // tree -> struct simple names whose fields are modified through a ref/out
    // parameter within that tree.
    private readonly Dictionary<SyntaxTree, HashSet<string>> _refModifiedStructsByTree;

    private CrossFileAccessIndex(
        HashSet<ITypeSymbol> refOutExternalWriteStructs,
        Dictionary<string, HashSet<string>> externalRefFields,
        Dictionary<string, HashSet<string>> externalAssignedFields,
        Dictionary<SyntaxTree, HashSet<string>> refModifiedStructsByTree)
    {
        _refOutExternalWriteStructs = refOutExternalWriteStructs;
        _externalRefFields = externalRefFields;
        _externalAssignedFields = externalAssignedFields;
        _refModifiedStructsByTree = refModifiedStructsByTree;
    }

    public static CrossFileAccessIndex Build(Compilation compilation)
    {
        var refOutWrite = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var refFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var assignedFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var refModifiedByTree = new Dictionary<SyntaxTree, HashSet<string>>();

        // Each tree's contribution is independent (only its own semantic model is
        // queried), so the scan runs in parallel across trees and the per-tree
        // results are merged as set/dictionary unions — order-independent and
        // identical to the sequential scan.
        System.Threading.Tasks.Parallel.ForEach(compilation.SyntaxTrees, tree =>
        {
            var localRefOutWrite = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var localRefFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var localAssignedFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var localModified = new HashSet<string>(StringComparer.Ordinal);

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // (1) ref/out/in parameters of methods belonging to other types whose
            // members are written >= 8 times (mirrors StructAnalyzer.HasExternalRefParamPassing).
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(method);
                if (methodSymbol == null) continue;
                foreach (var parameter in method.ParameterList.Parameters)
                {
                    if (!parameter.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.RefKeyword) ||
                            m.IsKind(SyntaxKind.OutKeyword) ||
                            m.IsKind(SyntaxKind.InKeyword)))
                        continue;

                    ITypeSymbol? type;
                    try
                    {
                        type = model.GetTypeInfo(parameter.Type).Type;
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    if (type == null) continue;

                    var target = type.OriginalDefinition;
                    if (SymbolEqualityComparer.Default.Equals(
                            methodSymbol.ContainingType?.OriginalDefinition, target))
                        continue; // the struct's own methods

                    var paramName = parameter.Identifier.Text;
                    var chainCount = 0;
                    foreach (var assign in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (IsParamMemberWrite(assign.Left, paramName)) chainCount++;
                    }
                    foreach (var unary in method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                                 .Cast<ExpressionSyntax>()
                                 .Concat(method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()))
                    {
                        if (unary is PrefixUnaryExpressionSyntax pre && IsParamMemberWrite(pre.Operand, paramName) ||
                            unary is PostfixUnaryExpressionSyntax post && IsParamMemberWrite(post.Operand, paramName))
                        {
                            chainCount++;
                        }
                    }
                    if (chainCount >= 8)
                        localRefOutWrite.Add(target);
                }
            }

            // (2) ref/out accesses to a struct field from outside the struct definition.
            foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                    !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    continue;
                var expression = argument.Expression;
                if (expression is PostfixUnaryExpressionSyntax suppress &&
                    suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                    expression = suppress.Operand;
                if (expression is not MemberAccessExpressionSyntax memberAccess) continue;
                if (model.GetSymbolInfo(memberAccess).Symbol is not IFieldSymbol field) continue;
                AddExternalField(localRefFields, localAssignedFields, field, argument);
            }

            // (3) object initializers assigning struct fields from outside.
            foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeInfo = model.GetTypeInfo(objectCreation.Type);
                var typeName = typeInfo.Type?.Name;
                if (typeName == null)
                    typeName = model.GetSymbolInfo(objectCreation.Type).Symbol?.Name;
                if (typeName == null)
                {
                    typeName = objectCreation.Type.ToString();
                    var genericIndex = typeName.IndexOf('<');
                    if (genericIndex > 0) typeName = typeName.Substring(0, genericIndex);
                }
                if (typeName == null || objectCreation.Initializer == null) continue;

                foreach (var expr in objectCreation.Initializer.Expressions)
                {
                    if (expr is not AssignmentExpressionSyntax initAssignment ||
                        initAssignment.Left is not IdentifierNameSyntax initFieldName)
                        continue;
                    AddExternalFieldName(null, localAssignedFields, initFieldName.Identifier.Text, typeName, objectCreation);
                }
            }

            // (4) assignments to struct fields from outside.
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not MemberAccessExpressionSyntax) continue;
                if (model.GetSymbolInfo(assignment.Left).Symbol is not IFieldSymbol field) continue;
                AddExternalField(null, localAssignedFields, field, assignment);
            }

            // (5) ++/-- on struct fields from outside.
            foreach (var unary in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
            {
                if (unary.Operand is not MemberAccessExpressionSyntax) continue;
                if (model.GetSymbolInfo(unary.Operand).Symbol is not IFieldSymbol field) continue;
                AddExternalField(null, localAssignedFields, field, unary);
            }
            foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
            {
                if (unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                    continue;
                if (unary.Operand is not MemberAccessExpressionSyntax) continue;
                if (model.GetSymbolInfo(unary.Operand).Symbol is not IFieldSymbol field) continue;
                AddExternalField(null, localAssignedFields, field, unary);
            }

            // (6) ref/out parameters of this tree whose members are modified (mirrors
            // CallGraphBuilder.IsModifiedThroughRef over the same tree).
            foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (!param.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)))
                    continue;
                var paramSymbol = model.GetDeclaredSymbol(param);
                if (paramSymbol?.Type is not INamedTypeSymbol paramType) continue;
                var containingMethod = param.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                if (containingMethod == null) continue;
                if (IsParameterFieldModified(containingMethod, param.Identifier.Text))
                    localModified.Add(paramType.Name);
            }

            lock (refOutWrite)
            {
                foreach (var t in localRefOutWrite) refOutWrite.Add(t);
            }
            lock (refFields)
            {
                MergeInto(refFields, localRefFields);
            }
            lock (assignedFields)
            {
                MergeInto(assignedFields, localAssignedFields);
            }
            if (localModified.Count > 0)
            {
                lock (refModifiedByTree)
                {
                    refModifiedByTree[tree] = localModified;
                }
            }
        });

        return new CrossFileAccessIndex(refOutWrite, refFields, assignedFields, refModifiedByTree);
    }

    private static void MergeInto(Dictionary<string, HashSet<string>> target, Dictionary<string, HashSet<string>> source)
    {
        foreach (var (key, values) in source)
        {
            if (!target.TryGetValue(key, out var set))
                target[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.UnionWith(values);
        }
    }

    public bool HasExternalRefParamPassing(INamedTypeSymbol structSymbol)
        => _refOutExternalWriteStructs.Contains(structSymbol.OriginalDefinition);

    public bool HasExternalRefFieldAccess(INamedTypeSymbol structSymbol)
    {
        var accessibleFields = AccessibleFieldNames(structSymbol);
        if (accessibleFields.Count == 0) return false;
        return _externalRefFields.TryGetValue(structSymbol.Name, out var set) && set.Overlaps(accessibleFields);
    }

    public bool HasExternalPublicFieldAssignment(INamedTypeSymbol structSymbol)
    {
        var accessibleFields = AccessibleFieldNames(structSymbol);
        if (accessibleFields.Count == 0) return false;
        return _externalAssignedFields.TryGetValue(structSymbol.Name, out var set) && set.Overlaps(accessibleFields);
    }

    public bool IsModifiedThroughRef(SyntaxTree tree, string structName)
        => _refModifiedStructsByTree.TryGetValue(tree, out var set) && set.Contains(structName);

    private static HashSet<string> AccessibleFieldNames(INamedTypeSymbol structSymbol)
        => structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private &&
                        f.DeclaredAccessibility != Accessibility.NotApplicable &&
                        !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

    private static void AddExternalField(
        Dictionary<string, HashSet<string>>? refFields,
        Dictionary<string, HashSet<string>> assignedFields,
        IFieldSymbol field,
        SyntaxNode accessNode)
    {
        var containingType = field.ContainingType;
        if (containingType == null) return;
        AddExternalFieldName(refFields, assignedFields, field.Name, containingType.Name, accessNode);
    }

    private static void AddExternalFieldName(
        Dictionary<string, HashSet<string>>? refFields,
        Dictionary<string, HashSet<string>> assignedFields,
        string fieldName,
        string structName,
        SyntaxNode accessNode)
    {
        var containingStruct = accessNode.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
        if (containingStruct != null && containingStruct.Identifier.Text == structName)
            return; // access inside the struct itself — not external
        if (refFields != null) Add(refFields, structName, fieldName);
        Add(assignedFields, structName, fieldName);
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }

    private static bool IsParamMemberWrite(ExpressionSyntax expr, string paramName)
    {
        while (expr is MemberAccessExpressionSyntax ma)
            expr = ma.Expression;
        return expr is IdentifierNameSyntax id && id.Identifier.Text == paramName;
    }

    private static bool IsParameterFieldModified(BaseMethodDeclarationSyntax method, string paramName)
    {
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }
        foreach (var postfix in method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            if (postfix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }
        foreach (var prefix in method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression &&
                prefix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression &&
                assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }
        return false;
    }
}
