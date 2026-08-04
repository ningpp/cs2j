using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class ReadOnlyStructRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<StructDeclarationSyntax, AnalyzeResult> _results;
    private readonly ReadOnlyStructMakerOptions _options;

    public ReadOnlyStructRewriter(Dictionary<StructDeclarationSyntax, AnalyzeResult> results,
        ReadOnlyStructMakerOptions options)
    {
        _results = results;
        _options = options;
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!_results.TryGetValue(node, out var result) || !result.ShouldRewrite)
            return base.VisitStructDeclaration(node);

        var rewritten = result.Level switch
        {
            ConversionLevel.DirectAdd => result.NeedsWithMethods
                ? AddCtorBasedWithMethods(ApplyDirectAdd(node))
                : ApplyDirectAdd(node),
            ConversionLevel.PropertyConvert => ApplyPropertyConvert(node),
            ConversionLevel.DataContainer => ApplyDataContainer(node),
            ConversionLevel.PublicFieldToProperty => ApplyPublicFieldToProperty(node),
            ConversionLevel.MethodMigrate => ApplyMethodMigrate(node, result),
            _ => node
        };

        return base.VisitStructDeclaration(rewritten);
    }

    internal static StructDeclarationSyntax ApplyDirectAdd(StructDeclarationSyntax node)
    {
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            return node;

        // C# requires all instance fields of a readonly struct to be readonly (CS8340).
        // Add the readonly modifier to every instance field that doesn't already have it.
        node = MarkInstanceFieldsReadOnly(node);

        var readonlyToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        if (node.Modifiers.Count > 0)
        {
            // Insert readonly as the last modifier (before 'struct' keyword)
            return node.AddModifiers(readonlyToken);
        }

        // No modifiers: insert before 'struct' keyword, preserving leading trivia
        var leadingTrivia = node.Keyword.LeadingTrivia;
        readonlyToken = readonlyToken.WithLeadingTrivia(leadingTrivia);
        var newKeyword = node.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList());
        return node.WithKeyword(newKeyword).AddModifiers(readonlyToken);
    }

    /// <summary>
    /// Adds the <c>readonly</c> modifier to every instance field (non-static, non-const)
    /// that doesn't already have it. Required so the rewritten C# compiles after the
    /// containing struct is marked <c>readonly</c> (CS8340).
    /// </summary>
    private static StructDeclarationSyntax MarkInstanceFieldsReadOnly(StructDeclarationSyntax node)
    {
        var readonlyFieldToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newMembers = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is FieldDeclarationSyntax field
                && !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))
                && !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))
                && !field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                newMembers = newMembers.Add(field.AddModifiers(readonlyFieldToken));
            }
            else
            {
                newMembers = newMembers.Add(member);
            }
        }
        return node.WithMembers(newMembers);
    }

    /// <summary>
    /// Adds ctor-based WithXxx methods for every instance field of an otherwise
    /// immutable (DirectAdd) readonly struct. Required when fields are passed as
    /// ref/out arguments elsewhere: the hoisting pass rewrites such sites into
    /// temp + WithXxx write-back (CS0192).
    /// </summary>
    private static StructDeclarationSyntax AddCtorBasedWithMethods(StructDeclarationSyntax node)
    {
        var structName = node.Identifier.Text;
        var instanceFieldTypes = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
        foreach (var f in node.Members.OfType<FieldDeclarationSyntax>()
                     .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword))))
        {
            foreach (var v in f.Declaration.Variables)
                instanceFieldTypes[v.Identifier.Text] = f.Declaration.Type;
        }
        if (instanceFieldTypes.Count == 0) return node;

        var composeCtor = FindPureConstructor(node, instanceFieldTypes.Keys);
        if (composeCtor == null)
        {
            var generated = GenerateComposeConstructor(structName, instanceFieldTypes, node);
            if (generated == null) return node;
            node = node.AddMembers(generated);
            composeCtor = generated;
        }

        var existingWith = node.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text)
            .Where(n => n.StartsWith("With", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var withMethods = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members.OfType<FieldDeclarationSyntax>().ToList())
        {
            if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            var isPrivate = member.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
            foreach (var v in member.Declaration.Variables)
            {
                if (!instanceFieldTypes.ContainsKey(v.Identifier.Text)) continue;
                if (existingWith.Contains("With" + v.Identifier.Text)) continue;
                withMethods.Add(GenerateWithMethodUsingConstructor(
                    structName, v.Identifier.Text, instanceFieldTypes, composeCtor,
                    isInternal: !isPrivate));
            }
        }

        return withMethods.Count > 0 ? node.AddMembers(withMethods.ToArray()) : node;
    }

    private static string MethodSignatureKey(MethodDeclarationSyntax method) =>
        string.Join(",", method.ParameterList.Parameters.Select(p =>
            (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref " : "") +
            (p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out " : "") +
            (p.Type?.ToString() ?? "")));

    private static StructDeclarationSyntax ApplyPropertyConvert(StructDeclarationSyntax node)
    {
        // L2: remove 'private set' from accessors, then add readonly
        var rewriter = new PrivateSetRemover();
        var result = (StructDeclarationSyntax)rewriter.Visit(node)!;
        return ApplyDirectAdd(result);
    }

    private StructDeclarationSyntax ApplyDataContainer(StructDeclarationSyntax node)
    {
        // L3: convert get/set properties to get-only, fields to properties, generate ctor
        var structName = node.Identifier.Text;
        var membersForCtor = new List<(string Name, TypeSyntax Type, bool IsPublic)>();
        var newMembers = new SyntaxList<MemberDeclarationSyntax>();

        foreach (var member in node.Members)
        {
            if (member is PropertyDeclarationSyntax prop &&
                prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
            {
                // Convert get/set → get-only
                var getter = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                var newProp = prop.WithAccessorList(
                    SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
                newMembers = newMembers.Add(newProp);
                // Only add to constructor members if the getter is an auto-property
                // (no body, no expression body). Properties with custom getter bodies
                // (e.g., get { return field; }) delegate to fields and cannot be assigned
                // in a constructor after the setter is removed. Adding them would also
                // cause duplicate parameter names when the property name camel-cases to
                // the same identifier as the backing field (e.g., property "A" → param "a"
                // colliding with field "a" → param "a").
                if (getter.Body == null && getter.ExpressionBody == null)
                {
                    membersForCtor.Add((prop.Identifier.Text, prop.Type,
                        prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))));
                }
            }
            else if (member is FieldDeclarationSyntax field &&
                     !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)) &&
                     !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                // Convert internal/private fields → get-only properties
                foreach (var variable in field.Declaration.Variables)
                {
                    var fieldProp = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                        .WithModifiers(field.Modifiers)
                        .WithAccessorList(SyntaxFactory.AccessorList(
                            SyntaxFactory.SingletonList(
                                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                        .WithTrailingTrivia(variable.Identifier.TrailingTrivia);
                    newMembers = newMembers.Add(fieldProp);
                    membersForCtor.Add((variable.Identifier.Text, field.Declaration.Type, false));
                }
            }
            else
            {
                newMembers = newMembers.Add(member);
            }
        }

        var rewritten = node.WithMembers(newMembers);

        // Generate constructor if no equivalent exists
        if (membersForCtor.Count > 0)
        {
            var ctor = ConstructorGenerator.Generate(structName, membersForCtor);
            if (ctor != null && !HasEquivalentCtor(rewritten, ctor))
            {
                rewritten = rewritten.AddMembers(ctor);
            }
        }

        return ApplyDirectAdd(rewritten);
    }

    /// <summary>
    /// L4: Convert non-private fields (public/internal/protected) to get-only properties
    /// and add readonly modifier.
    /// Generates WithXxx methods that use constructor to create new instances.
    /// Call sites are updated separately by AssignmentRewriter.
    /// 
    /// IMPORTANT: Fields with preprocessor directives (#if/#else) in their leading trivia
    /// are NOT converted. This is because the #endif directive is typically in the next 
    /// member's leading trivia, and moving it would break the preprocessor directive balance.
    /// The C# compiler requires #if/#else/#endif blocks to be balanced within the same scope.
    /// </summary>
    private StructDeclarationSyntax ApplyPublicFieldToProperty(StructDeclarationSyntax node)
    {
        var structName = node.Identifier.Text;

        // 1. Collect all fields (public and private)
        var allFields = node.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (Field: f, Variable: v)))
            .ToList();

        // Non-private instance fields are conversion candidates (public + internal + protected).
        var publicFields = allFields.Where(f =>
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) &&
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))).ToList();
        if (publicFields.Count == 0) return node;

        // Collect ALL non-private fields (public + internal) for WithXxx generation
        var nonPrivateFields = allFields.Where(f =>
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) &&
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
            !f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))).ToList();
        var allNonPrivateFieldTypes = nonPrivateFields.ToDictionary(
            f => f.Variable.Identifier.Text,
            f => f.Field.Declaration.Type);
        // Track which fields are internal (for access modifier on WithXxx methods)
        var internalFieldNames = nonPrivateFields
            .Where(f => f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
            .Select(f => f.Variable.Identifier.Text).ToHashSet();

        // 1b. Filter out fields that have preprocessor directives in their leading trivia
        // These fields cannot be safely converted to properties because the #endif directive
        // is typically in the next member's leading trivia, and moving it would break the
        // preprocessor directive balance.
        var convertibleFields = publicFields.Where(f => !FieldHasPreprocessorDirective(f.Field)).ToList();
        if (convertibleFields.Count == 0)
        {
            // All public fields have preprocessor directives - keep fields as-is,
            // add readonly modifier, and generate WithXxx methods for external write access.
            var readonlyNode = ApplyDirectAdd(node);

            // Ensure a full constructor exists for all non-private fields
            var allCtorMembersPreproc = allNonPrivateFieldTypes.Select(kvp =>
                (kvp.Key, kvp.Value, !internalFieldNames.Contains(kvp.Key))).ToList();
            var fullCtorPreproc = ConstructorGenerator.Generate(structName, allCtorMembersPreproc);
            if (fullCtorPreproc != null)
            {
                var existingCtors = readonlyNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
                foreach (var ec in existingCtors)
                {
                    if (!CtorAssignsAllFieldsOrChains(ec, allNonPrivateFieldTypes.Keys))
                        readonlyNode = readonlyNode.RemoveNode(ec, SyntaxRemoveOptions.KeepNoTrivia)!;
                }
                if (!HasEquivalentCtor(readonlyNode, fullCtorPreproc))
                    readonlyNode = readonlyNode.AddMembers(fullCtorPreproc);
            }

            // Generate WithXxx methods for ALL non-private fields using constructor
            var existingCtor = readonlyNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            foreach (var fieldName in allNonPrivateFieldTypes.Keys)
            {
                var withMethod = GenerateWithMethodUsingConstructor(structName, fieldName, allNonPrivateFieldTypes, existingCtor,
                    isInternal: internalFieldNames.Contains(fieldName));
                readonlyNode = readonlyNode.AddMembers(withMethod);
            }

            return readonlyNode;
        }

        // 2. Build field name -> type mapping (only for convertible fields)
        var fieldTypes = convertibleFields.ToDictionary(
            f => f.Variable.Identifier.Text,
            f => f.Field.Declaration.Type);

        // 3. Convert members: public field -> get-only property
        var newMembers = new SyntaxList<MemberDeclarationSyntax>();
        var publicFieldNames = new List<string>();

        foreach (var member in node.Members)
        {
            // Skip non-private fields that have preprocessor directives
            if (member is FieldDeclarationSyntax field &&
                !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) &&
                !field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
                !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            {
                if (FieldHasPreprocessorDirective(field))
                {
                    // Keep the field as-is
                    newMembers = newMembers.Add(member);
                    continue;
                }

                // Preserve the original accessibility modifiers (public/internal/protected),
                // dropping 'readonly' which is meaningless on a get-only property.
                var propertyModifiers = field.Modifiers
                    .Where(m => !m.IsKind(SyntaxKind.ReadOnlyKeyword))
                    .ToList();
                if (propertyModifiers.Count == 0)
                {
                    propertyModifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space));
                }

                foreach (var variable in field.Declaration.Variables)
                {
                    // non-private field -> get-only property
                    var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                        .WithModifiers(SyntaxFactory.TokenList(propertyModifiers))
                        .WithAccessorList(SyntaxFactory.AccessorList(
                            SyntaxFactory.SingletonList(
                                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                    // Preserve leading trivia (doc comments, etc.)
                    prop = prop.WithLeadingTrivia(field.GetLeadingTrivia());
                    newMembers = newMembers.Add(prop);
                    publicFieldNames.Add(variable.Identifier.Text);
                }
            }
            else
            {
                newMembers = newMembers.Add(member);
            }
        }

        var result = node.WithMembers(newMembers);

        // 4. Add readonly modifier
        result = ApplyDirectAdd(result);

        // 5. Ensure a full constructor exists that assigns ALL non-private fields.
        // Readonly structs require all fields to be definitely assigned in every constructor.
        var allCtorMembers = allNonPrivateFieldTypes.Select(kvp =>
            (kvp.Key, kvp.Value, !internalFieldNames.Contains(kvp.Key))).ToList();
        var fullCtor = ConstructorGenerator.Generate(structName, allCtorMembers);
        if (fullCtor != null)
        {
            // Remove existing constructors that do not assign all converted fields
            // (they would cause CS0171). Constructors that assign every field — or
            // chain to another constructor via ': this(...)' — are kept so existing
            // call sites keep compiling.
            var existingCtors = result.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            foreach (var existingCtor in existingCtors)
            {
                if (!CtorAssignsAllFieldsOrChains(existingCtor, allNonPrivateFieldTypes.Keys))
                {
                    result = result.RemoveNode(existingCtor, SyntaxRemoveOptions.KeepNoTrivia)!;
                }
            }

            if (!HasEquivalentCtor(result, fullCtor))
                result = result.AddMembers(fullCtor);
        }

        // 6. Generate WithXxx methods using constructor
        // Include ALL non-private fields (public + internal) so external assignments
        // to internal fields are also rewritable via WithXxx.
        var ctor = result.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        foreach (var fieldName in allNonPrivateFieldTypes.Keys)
        {
            var withMethod = GenerateWithMethodUsingConstructor(structName, fieldName, allNonPrivateFieldTypes, ctor,
                isInternal: internalFieldNames.Contains(fieldName));
            result = result.AddMembers(withMethod);
        }

        return result;
    }

    /// <summary>
    /// Extracts parameter name to field name mapping from constructor body.
    /// Handles both "this.Field = param" and "Field = param" patterns.
    /// For example, if constructor has: X = xCoordinate; Y = yCoordinate;
    /// Returns: { "xCoordinate": "X", "yCoordinate": "Y" }
    /// </summary>
    private static Dictionary<string, string> ExtractConstructorParamToFieldMap(
        ConstructorDeclarationSyntax? ctor)
    {
        var map = new Dictionary<string, string>();
        if (ctor?.Body == null) return map;

        foreach (var stmt in ctor.Body.Statements.OfType<ExpressionStatementSyntax>())
        {
            if (stmt.Expression is not AssignmentExpressionSyntax assign)
                continue;
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;

            // Extract field name from left side
            string? fieldName = null;
            
            // Case 1: this.FieldName = param
            if (assign.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } leftMa &&
                leftMa.Name is IdentifierNameSyntax leftFieldName)
            {
                fieldName = leftFieldName.Identifier.Text;
            }
            // Case 2: FieldName = param (implicit this)
            else if (assign.Left is IdentifierNameSyntax leftId)
            {
                fieldName = leftId.Identifier.Text;
            }

            if (fieldName == null)
                continue;

            // Right side must be parameter name (identifier)
            if (assign.Right is not IdentifierNameSyntax paramName)
                continue;

            map[paramName.Identifier.Text] = fieldName;
        }

        return map;
    }

    /// <summary>
    /// Generates a WithXxx method that uses the constructor to create a new instance.
    /// Example: public Point WithX(double x) => new Point(xCoordinate: x, yCoordinate: this.Y);
    /// </summary>
    private static MethodDeclarationSyntax GenerateWithMethodUsingConstructor(
        string structName,
        string fieldName,
        Dictionary<string, TypeSyntax> fieldTypes,
        ConstructorDeclarationSyntax? ctor,
        bool isInternal = false,
        string? storageFieldName = null)
    {
        var withMethodName = "With" + fieldName;
        var paramName = ToCamelCase(fieldName);
        var storageName = storageFieldName ?? fieldName;

        // Extract parameter -> field mapping from constructor
        var paramToFieldMap = ExtractConstructorParamToFieldMap(ctor);

        // Build reverse mapping: field -> parameter
        var fieldToParamMap = paramToFieldMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        // If no mapping (constructor body is not simple assignments), use camelCase parameter names
        if (fieldToParamMap.Count == 0)
        {
            foreach (var name in fieldTypes.Keys)
                fieldToParamMap[name] = ToCamelCase(name);
        }

        // Build constructor arguments with named parameters
        var arguments = new List<ArgumentSyntax>();
        foreach (var name in fieldTypes.Keys)
        {
            ExpressionSyntax argValue;
            if (name == storageName)
            {
                // Use new value for the target field
                argValue = SyntaxFactory.IdentifierName(paramName);
            }
            else
            {
                // Get original value from this
                argValue = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(name));
            }

            // Use named parameter matching constructor's parameter name
            var paramNameForCtor = fieldToParamMap.GetValueOrDefault(name, ToCamelCase(name));
            arguments.Add(SyntaxFactory.Argument(argValue)
                .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(paramNameForCtor))));
        }

        // Compose constructors with a collision-avoiding tag parameter require the tag
        // to be passed explicitly (Java has no default parameter values).
        if (ctor != null &&
            ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == "__cs2jTag"))
        {
            arguments.Add(SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)))
                .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName("__cs2jTag"))));
        }

        var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
            .WithType(fieldTypes[storageName]);

        return SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(structName), withMethodName)
            .AddModifiers(SyntaxFactory.Token(isInternal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .AddParameterListParameters(param)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(structName))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(arguments)))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace("        "));
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private StructDeclarationSyntax ApplyMethodMigrate(StructDeclarationSyntax node, AnalyzeResult result)
    {
        var rewritten = node;
        var structName = node.Identifier.Text;

        // 0. When the struct exposes non-private fields (public/internal/protected),
        // convert them to get-only properties with WithXxx methods first (L4-style).
        // Track the converted property names: writes to them inside migrated methods
        // must be rewritten to WithXxx chains in step 4.
        var withTargets = new HashSet<string>(StringComparer.Ordinal);
        if (result.HasNonPrivateFields)
        {
            var nonPrivateBefore = rewritten.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword) ||
                                                  m.IsKind(SyntaxKind.StaticKeyword) ||
                                                  m.IsKind(SyntaxKind.ConstKeyword)))
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                .ToList();

            rewritten = ApplyPublicFieldToProperty(rewritten);

            foreach (var name in nonPrivateBefore) withTargets.Add(name);
        }

        // Collect instance field names (private storage) for migration.
        var fieldNames = rewritten.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet();
        var propertyNames = rewritten.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            .Select(p => p.Identifier.Text)
            .ToHashSet();

        // 1. Migrate mutating methods
        if (result.MethodMigrations != null)
        {
            var migratedMethodNames = result.MethodMigrations
                .Select(m => m.Method.Name).ToHashSet();
            var migrator = new MethodMigrator(structName, fieldNames, propertyNames, migratedMethodNames);
            foreach (var migration in result.MethodMigrations)
            {
                var migrated = migrator.Migrate(migration.Syntax);
                // Match by full parameter-type signature: overloads frequently share
                // name and arity (e.g. Rectangle.Add(Point) vs Add(Rectangle)).
                var targetSignature = MethodSignatureKey(migration.Syntax);
                var current = rewritten.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == migration.Syntax.Identifier.Text &&
                                         MethodSignatureKey(m) == targetSignature);
                if (current != null)
                {
                    rewritten = rewritten.ReplaceNode(current, migrated);
                }
            }
        }

        // 2a. Auto-properties with setters: make them get-only auto-properties. They stay
        // assignable inside constructors and are treated as storage members for the compose
        // constructor and ctor-based WithXxx generation (step 3b). No explicit backing
        // fields are created — that would leave the auto-property itself unassigned (CS0843).
        var setterProps = rewritten.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
            .ToList();
        var removedSetterProps = new HashSet<string>(StringComparer.Ordinal);
        var autoStorageProps = new List<(string PropName, TypeSyntax Type)>();
        foreach (var prop in setterProps)
        {
            var getter = prop.AccessorList!.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            if (getter == null || getter.Body != null || getter.ExpressionBody != null) continue;
            var setter = prop.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter == null || setter.Body != null || setter.ExpressionBody != null) continue;
            autoStorageProps.Add((prop.Identifier.Text, prop.Type));
        }
        if (autoStorageProps.Count > 0)
        {
            var newMembers = new SyntaxList<MemberDeclarationSyntax>();
            foreach (var member in rewritten.Members)
            {
                if (member is PropertyDeclarationSyntax prop &&
                    autoStorageProps.Any(a => a.PropName == prop.Identifier.Text))
                {
                    var getterOnly = prop.AccessorList!.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                    newMembers = newMembers.Add(prop.WithAccessorList(
                        SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getterOnly))));
                }
                else
                {
                    newMembers = newMembers.Add(member);
                }
            }
            rewritten = rewritten.WithMembers(newMembers);
        }

        // 2b. Properties with setters: remove the setter and build WithXxx methods.
        // Custom setter bodies (e.g. validation) become body-based WithXxx whose inner
        // field writes are rewritten to WithXxx chains in step 4. Auto-properties get
        // ctor-based WithXxx in step 3b.
        var withMethods = new List<MemberDeclarationSyntax>();
        var propToBackingField = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in setterProps)
        {
            removedSetterProps.Add(prop.Identifier.Text);
            var setter = prop.AccessorList!.Accessors.First(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            var withMethodName = "With" + prop.Identifier.Text;
            var withAccessibility = setter.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))
                ? SyntaxKind.PrivateKeyword
                : SyntaxKind.PublicKeyword;

            if (setter.Body != null)
            {
                // Detect the backing storage: if every assignment in the setter body
                // writes `value` into a single member, that member is the backing field.
                var assignedTargets = setter.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                                a.Right is IdentifierNameSyntax { Identifier.Text: "value" })
                    .Select(a => a.Left switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
                        _ => null
                    })
                    .Where(n => n != null)
                    .Distinct()
                    .ToList();
                if (assignedTargets.Count == 1)
                    propToBackingField[prop.Identifier.Text] = assignedTargets[0]!;

                var setterBody = (BlockSyntax)new ThisToResultRewriter().Visit(setter.Body)!;
                var resultDecl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator("result")
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.ThisExpression())))))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                var newBody = setterBody.WithStatements(setterBody.Statements.Insert(0, resultDecl));
                if (fieldNames.Count > 0 || propertyNames.Count > 0)
                {
                    newBody = (BlockSyntax)new MethodMigrator.ImplicitFieldToResultRewriter(fieldNames, propertyNames).Visit(newBody)!;
                }
                newBody = newBody.AddStatements(
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                        .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                withMethods.Add(SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName(structName), withMethodName)
                    .AddModifiers(SyntaxFactory.Token(withAccessibility).WithTrailingTrivia(SyntaxFactory.Space))
                    .AddParameterListParameters(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(prop.Type))
                    .WithBody(newBody));
            }

            var getterOnly = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var currentProp = rewritten.Members.OfType<PropertyDeclarationSyntax>()
                .First(p => p.Identifier.Text == prop.Identifier.Text);
            rewritten = rewritten.ReplaceNode(currentProp,
                currentProp.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getterOnly))));
        }

        // 2c. Constructor assignments to removed-setter properties with a known backing
        // field are redirected to the backing field (the property is now get-only and
        // cannot be assigned, CS0200). Auto-property assignments in ctors stay as-is.
        if (propToBackingField.Count > 0)
        {
            rewritten = (StructDeclarationSyntax)new CtorPropertyToBackingFieldRewriter(propToBackingField).Visit(rewritten)!;
        }

        // 3. Compose constructor: a constructor that directly assigns EVERY storage member
        // (fields + get-only auto-properties). Reuse an existing pure constructor when
        // available; otherwise generate a private one (extra tag parameter when the
        // signature would collide).
        var instanceFieldTypes = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
        foreach (var f in rewritten.Members.OfType<FieldDeclarationSyntax>()
                     .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword))))
        {
            foreach (var v in f.Declaration.Variables)
                instanceFieldTypes[v.Identifier.Text] = f.Declaration.Type;
        }
        foreach (var ap in autoStorageProps)
            instanceFieldTypes[ap.PropName] = ap.Type;

        ConstructorDeclarationSyntax? composeCtor = null;
        if (instanceFieldTypes.Count > 0)
        {
            composeCtor = FindPureConstructor(rewritten, instanceFieldTypes.Keys);
            if (composeCtor == null)
            {
                var generated = GenerateComposeConstructor(structName, instanceFieldTypes, rewritten);
                if (generated != null)
                {
                    rewritten = rewritten.AddMembers(generated);
                    composeCtor = generated;
                }
            }
        }

        // 3b. Ctor-based WithXxx methods for remaining fields and auto-property backings.
        if (composeCtor != null)
        {
            var existingWith = rewritten.Members.OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .Where(n => n.StartsWith("With", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var member in rewritten.Members.OfType<FieldDeclarationSyntax>().ToList())
            {
                // Skip static and const fields — they are not instance storage and are
                // absent from instanceFieldTypes (a lookup would throw).
                if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                    continue;
                var isPrivate = member.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
                foreach (var v in member.Declaration.Variables)
                {
                    if (!instanceFieldTypes.ContainsKey(v.Identifier.Text)) continue;
                    if (existingWith.Contains("With" + v.Identifier.Text)) continue;
                    withMethods.Add(GenerateWithMethodUsingConstructor(
                        structName, v.Identifier.Text, instanceFieldTypes, composeCtor,
                        isInternal: !isPrivate));
                }
            }

            foreach (var backing in autoStorageProps)
            {
                if (existingWith.Contains("With" + backing.PropName)) continue;
                withMethods.Add(GenerateWithMethodUsingConstructor(
                    structName, backing.PropName, instanceFieldTypes, composeCtor,
                    isInternal: false));
            }
        }

        if (withMethods.Count > 0)
        {
            rewritten = rewritten.AddMembers(withMethods.ToArray());
        }

        // 4. Rewrite all remaining writes through result/this into WithXxx chains
        // (direct writes to readonly fields are illegal even through local copies).
        foreach (var name in instanceFieldTypes.Keys) withTargets.Add(name);
        foreach (var name in removedSetterProps) withTargets.Add(name);
        if (withTargets.Count > 0)
        {
            var fieldWithRewriter = new FieldAssignmentToWithRewriter(withTargets);
            rewritten = (StructDeclarationSyntax)fieldWithRewriter.Visit(rewritten)!;
        }

        // Generate explicit interface implementations for migrated methods that
        // implicitly implemented interface members. Migration changes their signature
        // (void → struct return, or an added out parameter), so an explicit impl
        // delegating to the migrated method keeps the interface contract intact.
        if (result.InterfaceMigrations is { Count: > 0 })
        {
            rewritten = AddExplicitInterfaceImplementations(rewritten, result.InterfaceMigrations);
        }

        // Normalize constructors so each field is assigned exactly once (Java final
        // fields allow a single assignment), then mark the struct readonly.
        rewritten = ConstructorNormalizer.NormalizeStruct(rewritten);
        return ApplyDirectAdd(rewritten);
    }

    /// <summary>
    /// Finds an existing constructor that directly assigns every field from parameters
    /// (no method calls, no chaining) — suitable as the compose constructor for
    /// ctor-based WithXxx methods.
    /// </summary>
    private static ConstructorDeclarationSyntax? FindPureConstructor(
        StructDeclarationSyntax node, IEnumerable<string> fieldNames)
    {
        var required = new HashSet<string>(fieldNames, StringComparer.Ordinal);
        foreach (var ctor in node.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.Body == null) continue;
            if (ctor.Initializer != null) continue;
            // Method invocations (e.g. Add(...)) make the constructor non-pure.
            if (ctor.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any()) continue;

            var map = ExtractConstructorParamToFieldMap(ctor);
            if (map.Count == required.Count && required.All(map.ContainsValue))
                return ctor;
        }
        return null;
    }

    /// <summary>
    /// Generates a private constructor assigning every instance field directly. When the
    /// parameter-type list collides with an existing constructor, an extra
    /// <c>int __cs2jTag</c> parameter disambiguates the overload (callers use named
    /// arguments and pass <c>__cs2jTag: 0</c>).
    /// </summary>
    private static ConstructorDeclarationSyntax? GenerateComposeConstructor(
        string structName,
        Dictionary<string, TypeSyntax> instanceFieldTypes,
        StructDeclarationSyntax node)
    {
        if (instanceFieldTypes.Count == 0) return null;

        var needsTag = node.Members.OfType<ConstructorDeclarationSyntax>().Any(c =>
            c.ParameterList.Parameters.Count == instanceFieldTypes.Count &&
            c.ParameterList.Parameters.Select(p => p.Type?.ToString())
                .SequenceEqual(instanceFieldTypes.Values.Select(t => t.ToString())));

        var parameters = instanceFieldTypes
            .Select(kvp => SyntaxFactory.Parameter(SyntaxFactory.Identifier(ToCamelCase(kvp.Key)))
                .WithType(kvp.Value))
            .ToList();

        var statements = instanceFieldTypes
            .Select(kvp => (StatementSyntax)SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(kvp.Key)),
                    SyntaxFactory.IdentifierName(ToCamelCase(kvp.Key)))))
            .ToList();

        if (needsTag)
        {
            // The tag disambiguates the compose constructor from an existing same-signature
            // constructor. It is passed explicitly (0) by generated WithXxx calls — no
            // default value, because Java (the conversion target) has no optional params
            // and would drop a constructor with defaulted parameters.
            parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier("__cs2jTag"))
                .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))));
        }

        return SyntaxFactory.ConstructorDeclaration(structName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(statements));
    }

    /// <summary>
    /// Generates explicit interface implementations for migrated methods. Example:
    /// <code>
    /// void IRectangle&lt;Point&gt;.Add(Point point) { this = Add(point); }
    /// </code>
    /// Assigning to <c>this</c> is legal in structs and preserves the original
    /// in-place mutation semantics for callers dispatching through the interface.
    /// </summary>
    private static StructDeclarationSyntax AddExplicitInterfaceImplementations(
        StructDeclarationSyntax node,
        IReadOnlyList<InterfaceMigrationInfo> interfaceMigrations)
    {
        foreach (var info in interfaceMigrations)
        {
            var originalSyntax = info.Method.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (originalSyntax == null) continue;

            var ifaceTypeName = info.InterfaceMember.ContainingType
                .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var memberName = info.InterfaceMember.Name;

            // Idempotency: skip when an explicit implementation already exists.
            // Explicit interface syntax: `void IFace.Member(args)` — the specifier
            // holds only the interface type, the method identifier holds the member name.
            // Overloads share both the member name and possibly the arity, so the full
            // parameter type list disambiguates.
            var originalParamKey = MethodSignatureKey(originalSyntax);
            if (node.Members.OfType<MethodDeclarationSyntax>().Any(m =>
                m.ExplicitInterfaceSpecifier?.Name.ToString() == ifaceTypeName &&
                m.Identifier.Text == memberName &&
                MethodSignatureKey(m) == originalParamKey))
                continue;

            var parameters = originalSyntax.ParameterList;
            var arguments = parameters.Parameters
                .Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier.Text)))
                .ToList();

            MethodDeclarationSyntax explicitImpl;
            if (info.Type == MigrationType.VoidToStruct)
            {
                // void IFace.M(args) { M(args); }
                // In a readonly struct `this = ...` is illegal inside methods (CS1604),
                // so the migrated result cannot be written back through the interface.
                // The call is kept (result discarded) to satisfy the interface contract.
                var call = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.IdentifierName(originalSyntax.Identifier.Text),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
                var body = SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(call));
                explicitImpl = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                        memberName)
                    .WithExplicitInterfaceSpecifier(
                        SyntaxFactory.ExplicitInterfaceSpecifier(SyntaxFactory.ParseName(ifaceTypeName)))
                    .WithParameterList(parameters)
                    .WithBody(body);
            }
            else
            {
                // OtherReturnToOut: retType IFace.M(args) {
                //   var __ret = M(args, out _); return __ret; }
                // The out result cannot be written back in a readonly struct method
                // (CS1604), so it is discarded via a `_` designation.
                var outArgument = SyntaxFactory.Argument(
                        SyntaxFactory.DeclarationExpression(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.DiscardDesignation(
                                SyntaxFactory.Identifier("_"))))
                    .WithRefKindKeyword(
                        SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space));
                var callArguments = arguments.Append(outArgument).ToList();
                var call = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.IdentifierName(originalSyntax.Identifier.Text),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(callArguments)));
            
                var returnType = info.InterfaceMember.ReturnType.SpecialType == SpecialType.System_Void
                    ? (TypeSyntax)SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
                    : SyntaxFactory.ParseTypeName(
                        info.InterfaceMember.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            
                var statements = new List<StatementSyntax>();
                if (info.InterfaceMember.ReturnType.SpecialType != SpecialType.System_Void)
                {
                    statements.Add(SyntaxFactory.ReturnStatement(call));
                }
                else
                {
                    statements.Add(SyntaxFactory.ExpressionStatement(call));
                }
            
                explicitImpl = SyntaxFactory.MethodDeclaration(returnType, memberName)
                    .WithExplicitInterfaceSpecifier(
                        SyntaxFactory.ExplicitInterfaceSpecifier(SyntaxFactory.ParseName(ifaceTypeName)))
                    .WithParameterList(parameters)
                    .WithBody(SyntaxFactory.Block(statements));
            }

            node = node.AddMembers(explicitImpl);
        }

        return node;
    }

    /// <summary>
    /// Rewrites constructor assignments to removed-setter properties into assignments
    /// to their backing fields: <c>this.Weight = w;</c> → <c>this.borderWeight = w;</c>.
    /// Only applies inside constructors (get-only auto-properties are assignable there,
    /// but custom get-only properties are not — CS0200).
    /// </summary>
    private sealed class CtorPropertyToBackingFieldRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _propToField;

        public CtorPropertyToBackingFieldRewriter(Dictionary<string, string> propToField)
        {
            _propToField = propToField;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => node;

        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                node.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma &&
                ma.Name is IdentifierNameSyntax propName &&
                _propToField.TryGetValue(propName.Identifier.Text, out var backingField))
            {
                var newLeft = ma.WithName(
                    SyntaxFactory.IdentifierName(backingField).WithTriviaFrom(propName));
                return node.WithLeft(newLeft);
            }

            return base.VisitAssignmentExpression(node);
        }
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            => SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
    }

    /// <summary>
    /// Returns true when the constructor definitely assigns all of the given fields
    /// (directly, through chained assignments, or by chaining to another constructor
    /// via ': this(...)').
    /// </summary>
    private static bool CtorAssignsAllFieldsOrChains(
        ConstructorDeclarationSyntax ctor, IEnumerable<string> fieldNames)
    {
        if (ctor.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true)
            return true;

        if (ctor.Body == null)
            return false;

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in ctor.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var name = assignment.Left switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (name != null)
                assigned.Add(name);
        }

        return fieldNames.All(assigned.Contains);
    }

    private static bool HasEquivalentCtor(StructDeclarationSyntax node, ConstructorDeclarationSyntax newCtor)
    {
        var newParams = newCtor.ParameterList.Parameters;
        return node.Members.OfType<ConstructorDeclarationSyntax>().Any(existing =>
        {
            var ep = existing.ParameterList.Parameters;
            if (ep.Count != newParams.Count) return false;
            for (int i = 0; i < ep.Count; i++)
                if (ep[i].Type?.ToString() != newParams[i].Type?.ToString()) return false;
            return true;
        });
    }

    private sealed class PrivateSetRemover : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAccessorList(AccessorListSyntax node)
        {
            var accessors = node.Accessors.Where(a =>
                !(a.IsKind(SyntaxKind.SetAccessorDeclaration) &&
                  a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))));
            return node.WithAccessors(SyntaxFactory.List(accessors));
        }
    }

    /// <summary>
    /// Replaces property assignments (this.Prop = v / result.Prop = v) with
    /// backing field assignments (this.field = v / result.field = v) for properties
    /// whose setters were removed during L5 migration.
    /// </summary>
    private sealed class PropertyToFieldAssignmentRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _propToField;

        public PropertyToFieldAssignmentRewriter(Dictionary<string, string> propToField)
        {
            _propToField = propToField;
        }

        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            if (node.Left is MemberAccessExpressionSyntax ma
                && ma.Name is IdentifierNameSyntax propName
                && _propToField.TryGetValue(propName.Identifier.Text, out var fieldName))
            {
                // Convert this.Prop = v OR result.Prop = v to this.field = v / result.field = v
                if (ma.Expression is ThisExpressionSyntax || ma.Expression is IdentifierNameSyntax { Identifier.Text: "result" })
                {
                    var newLeft = ma.WithName(
                        SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(propName));
                    return node.WithLeft(newLeft);
                }
            }

            return base.VisitAssignmentExpression(node);
        }
    }

    /// <summary>
    /// Rewrites writes to fields/removed-setter properties accessed through
    /// <c>result</c>/<c>this</c> into chained WithXxx calls. Direct writes are illegal
    /// in readonly structs even through local copies (CS0191), so every mutation must
    /// produce a new instance:
    /// <code>
    /// result.f = v;           → result = result.WithF(v);
    /// result.f op= v;         → result = result.WithF(result.f op v);
    /// result.f++;             → result = result.WithF(result.f + 1);
    /// arr[result.f++] = v;    → var t = result.f; result = result.WithF(t + 1); arr[t] = v;
    /// </code>
    /// Constructors are skipped (get-only auto-properties are assignable there, and
    /// WithXxx calls on <c>this</c> are forbidden before definite assignment — CS0188).
    /// </summary>
    private sealed class FieldAssignmentToWithRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _targets;
        private int _tempCounter;

        public FieldAssignmentToWithRewriter(HashSet<string> targets)
        {
            _targets = targets;
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => node;

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // Expression-bodied ctor-based WithXxx methods compose via constructor and
            // never assign fields — leave them untouched.
            if (node.ExpressionBody != null &&
                node.Identifier.Text.StartsWith("With", StringComparison.Ordinal))
                return node;

            return base.VisitMethodDeclaration(node);
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            // Rewrite nested content first (bottom-up).
            node = (BlockSyntax)base.VisitBlock(node)!;

            // Then hoist inline result.f++/-- occurrences (e.g. inside element-access
            // indices) into temps at this block level.
            var newStatements = new List<StatementSyntax>();
            foreach (var statement in node.Statements)
            {
                var hoister = new InlineUnaryHoister(this);
                var rewrittenStatement = (StatementSyntax)hoister.Visit(statement)!;
                newStatements.AddRange(hoister.Hoisted);
                newStatements.Add(rewrittenStatement);
            }
            return node.WithStatements(SyntaxFactory.List(newStatements));
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            if (node.Expression is PostfixUnaryExpressionSyntax postfix &&
                (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)) &&
                TryGetTargetAccess(postfix.Operand, out var access1))
            {
                return SyntaxFactory.ExpressionStatement(
                        BuildWithCall(access1, BuildUnaryValue(access1, postfix.IsKind(SyntaxKind.PostIncrementExpression))))
                    .WithTriviaFrom(node);
            }

            if (node.Expression is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)) &&
                TryGetTargetAccess(prefix.Operand, out var access2))
            {
                return SyntaxFactory.ExpressionStatement(
                        BuildWithCall(access2, BuildUnaryValue(access2, prefix.IsKind(SyntaxKind.PreIncrementExpression))))
                    .WithTriviaFrom(node);
            }

            if (node.Expression is AssignmentExpressionSyntax assignment)
            {
                var replaced = RewriteAssignment(assignment);
                if (replaced != null)
                    return SyntaxFactory.ExpressionStatement(replaced).WithTriviaFrom(node);
            }

            return base.VisitExpressionStatement(node);
        }

        private ExpressionSyntax? RewriteAssignment(AssignmentExpressionSyntax assignment)
        {
            if (!TryGetTargetAccess(assignment.Left, out var access))
                return null;

            var name = access.Name.Identifier.Text;
            ExpressionSyntax value;
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // Chained assignment: result.a = result.b = v
                // evaluates as b = v first, then a = v. Rewrite to a With chain:
                //   result = result.Withb(v).Witha(v);
                if (assignment.Right is AssignmentExpressionSyntax innerAssign)
                {
                    var chain = CollectChainedTargets(assignment);
                    if (chain != null)
                    {
                        var receiver = access.Expression.WithoutTrivia();
                        ExpressionSyntax expr = receiver;
                        for (var i = chain.Value.Names.Count - 1; i >= 0; i--)
                        {
                            expr = SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    expr,
                                    SyntaxFactory.IdentifierName("With" + chain.Value.Names[i])),
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SingletonSeparatedList(
                                        SyntaxFactory.Argument(chain.Value.FinalValue))));
                        }
                        return SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            receiver,
                            expr);
                    }
                }
                value = assignment.Right;
            }
            else
            {
                var binaryKind = assignment.Kind() switch
                {
                    SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                    SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                    SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                    SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
                    SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
                    SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                    SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                    SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
                    SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
                    SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
                    _ => (SyntaxKind?)null
                };
                if (binaryKind == null)
                    return null;

                var read = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    access.Expression.WithoutTrivia(),
                    SyntaxFactory.IdentifierName(name));
                value = SyntaxFactory.BinaryExpression(
                    binaryKind.Value,
                    read,
                    assignment.Right is BinaryExpressionSyntax
                        ? SyntaxFactory.ParenthesizedExpression(assignment.Right)
                        : assignment.Right);
            }

            return BuildWithCall(access, value);
        }

        /// <summary>
        /// Collects the target member names of a chained assignment
        /// (<c>result.a = result.b = v</c> → names [a, b], value v). Returns null when
        /// any link's right side is not another target assignment or a plain value.
        /// </summary>
        private (List<string> Names, ExpressionSyntax FinalValue)? CollectChainedTargets(
            AssignmentExpressionSyntax assignment)
        {
            var names = new List<string>();
            var current = assignment;
            while (true)
            {
                if (!current.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                    !TryGetTargetAccess(current.Left, out var acc))
                    return null;
                names.Add(acc.Name.Identifier.Text);
                if (current.Right is AssignmentExpressionSyntax next)
                {
                    current = next;
                    continue;
                }
                return (names, current.Right);
            }
        }

        private ExpressionSyntax BuildUnaryValue(MemberAccessExpressionSyntax access, bool isIncrement)
        {
            var read = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                access.Expression.WithoutTrivia(),
                access.Name.WithoutTrivia());
            return SyntaxFactory.BinaryExpression(
                isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                read,
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
        }

        private static ExpressionSyntax BuildWithCall(MemberAccessExpressionSyntax access, ExpressionSyntax value)
        {
            var name = access.Name.Identifier.Text;
            var receiver = access.Expression.WithoutTrivia();
            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                receiver,
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        receiver,
                        SyntaxFactory.IdentifierName("With" + name)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(value)))));
        }

        internal bool TryGetTargetAccess(ExpressionSyntax expression, out MemberAccessExpressionSyntax access)
        {
            access = null!;
            if (expression is MemberAccessExpressionSyntax ma &&
                ma.Name is IdentifierNameSyntax idName &&
                _targets.Contains(idName.Identifier.Text) &&
                (ma.Expression is ThisExpressionSyntax ||
                 ma.Expression is IdentifierNameSyntax { Identifier.Text: "result" }))
            {
                access = ma;
                return true;
            }
            return false;
        }

        internal string NextTempName(string fieldName)
            => "__cs2jOld" + char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1) + "_" + (_tempCounter++);

        /// <summary>
        /// Hoists inline <c>result.f++</c>/<c>--result.f</c> occurrences inside larger
        /// expressions into temp variables plus WithXxx statements.
        /// </summary>
        private sealed class InlineUnaryHoister : CSharpSyntaxRewriter
        {
            private readonly FieldAssignmentToWithRewriter _parent;
            internal readonly List<StatementSyntax> Hoisted = new();

            public InlineUnaryHoister(FieldAssignmentToWithRewriter parent)
            {
                _parent = parent;
            }

            public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
            {
                if ((node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression)) &&
                    _parent.TryGetTargetAccess(node.Operand, out var access))
                {
                    return Hoist(node, access, node.IsKind(SyntaxKind.PostIncrementExpression));
                }
                return base.VisitPostfixUnaryExpression(node);
            }

            public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
            {
                if ((node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression)) &&
                    _parent.TryGetTargetAccess(node.Operand, out var access))
                {
                    return Hoist(node, access, node.IsKind(SyntaxKind.PreIncrementExpression));
                }
                return base.VisitPrefixUnaryExpression(node);
            }

            private SyntaxNode Hoist(ExpressionSyntax node, MemberAccessExpressionSyntax access, bool isIncrement)
            {
                var fieldName = access.Name.Identifier.Text;
                var tempName = _parent.NextTempName(fieldName);

                Hoisted.Add(SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(tempName)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    access.WithoutTrivia()))))));
                Hoisted.Add(SyntaxFactory.ExpressionStatement(
                    _parent_BuildWithCall(access, tempName, isIncrement)));

                return SyntaxFactory.IdentifierName(tempName).WithTriviaFrom(node);
            }

            private static ExpressionSyntax _parent_BuildWithCall(
                MemberAccessExpressionSyntax access, string tempName, bool isIncrement)
            {
                var fieldName = access.Name.Identifier.Text;
                var receiver = access.Expression.WithoutTrivia();
                var value = SyntaxFactory.BinaryExpression(
                    isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                    SyntaxFactory.IdentifierName(tempName),
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
                return SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    receiver,
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            receiver,
                            SyntaxFactory.IdentifierName("With" + fieldName)),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(value)))));
            }
        }
    }

    #region Preprocessor Directive Helpers

    /// <summary>
    /// Checks if the trivia list contains any preprocessor directives (#if, #else, #elif, #endif).
    /// </summary>
    private static bool HasPreprocessorDirective(SyntaxTriviaList trivia)
    {
        foreach (var t in trivia)
        {
            if (t.IsKind(SyntaxKind.IfDirectiveTrivia) ||
                t.IsKind(SyntaxKind.ElseDirectiveTrivia) ||
                t.IsKind(SyntaxKind.ElifDirectiveTrivia) ||
                t.IsKind(SyntaxKind.EndIfDirectiveTrivia))
            {
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Checks if the field declaration has preprocessor directives in its leading trivia.
    /// This checks all descendant tokens for preprocessor directives.
    /// </summary>
    private static bool FieldHasPreprocessorDirective(FieldDeclarationSyntax field)
    {
        // Check all descendant tokens for preprocessor directives
        foreach (var token in field.DescendantTokens())
        {
            if (token.HasLeadingTrivia)
            {
                if (HasPreprocessorDirective(token.LeadingTrivia))
                    return true;
            }
        }
        
        return false;
    }

    #endregion
}
