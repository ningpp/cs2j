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
            ConversionLevel.DirectAdd => ApplyDirectAdd(node),
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
    /// L4: Convert public fields to get-only properties and add readonly modifier.
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

        var publicFields = allFields.Where(f => f.Field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))).ToList();
        if (publicFields.Count == 0) return node;

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

            // Build field types from ALL public fields (including preprocessor-guarded ones)
            var allFieldTypes = publicFields.ToDictionary(
                f => f.Variable.Identifier.Text,
                f => f.Field.Declaration.Type);

            // Generate WithXxx methods using existing constructor
            var existingCtor = readonlyNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            foreach (var fieldName in allFieldTypes.Keys)
            {
                var withMethod = GenerateWithMethodUsingConstructor(structName, fieldName, allFieldTypes, existingCtor);
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
            // Skip public fields that have preprocessor directives
            if (member is FieldDeclarationSyntax field &&
                field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                if (FieldHasPreprocessorDirective(field))
                {
                    // Keep the field as-is
                    newMembers = newMembers.Add(member);
                    continue;
                }
                
                foreach (var variable in field.Declaration.Variables)
                {
                    // public field -> get-only property
                    var prop = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
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

        // 5. Ensure constructor exists
        var ctor = result.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null)
        {
            var ctorMembers = publicFieldNames.Select(n => (n, fieldTypes[n], true)).ToList();
            var newCtor = ConstructorGenerator.Generate(structName, ctorMembers);
            if (newCtor != null)
                result = result.AddMembers(newCtor);
        }

        // 6. Generate WithXxx methods using constructor
        ctor = result.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        foreach (var fieldName in publicFieldNames)
        {
            var withMethod = GenerateWithMethodUsingConstructor(structName, fieldName, fieldTypes, ctor);
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
        ConstructorDeclarationSyntax? ctor)
    {
        var withMethodName = "With" + fieldName;
        var paramName = ToCamelCase(fieldName);

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
            if (name == fieldName)
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

        var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
            .WithType(fieldTypes[fieldName]);

        return SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(structName), withMethodName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
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

        // Collect field names and property names for migration
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
                var current = rewritten.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == migration.Syntax.Identifier.Text &&
                                         m.ParameterList.Parameters.Count == migration.Syntax.ParameterList.Parameters.Count);
                if (current != null)
                {
                    rewritten = rewritten.ReplaceNode(current, migrated);
                }
            }
        }

        // 2. Migrate public property setters to WithXxx() methods
        var withMethods = new List<MemberDeclarationSyntax>();
        var propertiesToConvert = rewritten.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) &&
                !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true)
            .ToList();

        foreach (var prop in propertiesToConvert)
        {
            var setter = prop.AccessorList!.Accessors.First(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            var withMethodName = "With" + prop.Identifier.Text;

            // Build WithXxx method body from setter body
            MethodDeclarationSyntax withMethod;
            if (setter.Body != null)
            {
                // Setter has a body — migrate it
                var setterBody = (BlockSyntax)new ThisToResultRewriter().Visit(setter.Body)!;
                // Replace `value` parameter references (already correct in setter body)
                var resultDecl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator("result")
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.ThisExpression())))))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                // Replace field/property assignments on this with result.field/result.property
                var newBody = setterBody.WithStatements(
                    setterBody.Statements.Insert(0, resultDecl));

                // Use ImplicitFieldToResultRewriter to replace implicit field/property accesses
                if (fieldNames.Count > 0 || propertyNames.Count > 0)
                {
                    newBody = (BlockSyntax)new MethodMigrator.ImplicitFieldToResultRewriter(fieldNames, propertyNames).Visit(newBody)!;
                }

                var returnStmt = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                newBody = newBody.AddStatements(returnStmt);

                var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(prop.Type);

                withMethod = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName(structName), withMethodName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .AddParameterListParameters(param)
                    .WithBody(newBody);
            }
            else
            {
                // Auto-property setter — simple assignment
                // Note: For auto-properties, the setter body is empty (auto-generated).
                // We generate result.Property = value, but the property setter is removed
                // during migration. This is a known limitation for auto-properties.
                var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(prop.Type);

                var body = SyntaxFactory.Block(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator("result")
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.ThisExpression()))))),
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("result"),
                                SyntaxFactory.IdentifierName(prop.Identifier.Text)),
                            SyntaxFactory.IdentifierName("value"))),
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result")));

                withMethod = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName(structName), withMethodName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .AddParameterListParameters(param)
                    .WithBody(body);
            }

            withMethods.Add(withMethod);

            // Remove setter from property (make it get-only)
            var getter = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var newProp = prop.WithAccessorList(
                SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            rewritten = rewritten.ReplaceNode(
                rewritten.Members.OfType<PropertyDeclarationSyntax>()
                    .First(p => p.Identifier.Text == prop.Identifier.Text),
                newProp);
        }

        // Add WithXxx methods
        if (withMethods.Count > 0)
        {
            rewritten = rewritten.AddMembers(withMethods.ToArray());
        }

        // Replace internal property assignments with backing field assignments.
        // After removing setters, `this.Prop = v` must become `this.backingField = v`.
        var removedSetterProps = node.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
            .Select(p => p.Identifier.Text)
            .ToHashSet();
        if (removedSetterProps.Count > 0)
        {
            var allFieldNames = rewritten.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                .ToHashSet();
            var propToField = new Dictionary<string, string>();
            foreach (var propName in removedSetterProps)
            {
                // First: try to extract backing field from getter body (return this.field;)
                var propDecl = node.Members.OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == propName);
                var getter = propDecl?.AccessorList?.Accessors
                    .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                if (getter?.Body != null)
                {
                    var returnExpr = getter.Body.DescendantNodes()
                        .OfType<ReturnStatementSyntax>()
                        .Select(r => r.Expression)
                        .FirstOrDefault();
                    if (returnExpr is MemberAccessExpressionSyntax retMa
                        && retMa.Expression is ThisExpressionSyntax
                        && retMa.Name is IdentifierNameSyntax retField
                        && allFieldNames.Contains(retField.Identifier.Text))
                    {
                        propToField[propName] = retField.Identifier.Text;
                        continue;
                    }
                }
                // Also check expression-bodied getter: get => this.field;
                if (getter?.ExpressionBody != null)
                {
                    var expr = getter.ExpressionBody.Expression;
                    if (expr is MemberAccessExpressionSyntax exprMa
                        && exprMa.Expression is ThisExpressionSyntax
                        && exprMa.Name is IdentifierNameSyntax exprField
                        && allFieldNames.Contains(exprField.Identifier.Text))
                    {
                        propToField[propName] = exprField.Identifier.Text;
                        continue;
                    }
                }

                // Fallback: try common backing field patterns
                var candidates = new[]
                {
                    char.ToLowerInvariant(propName[0]) + propName[1..],
                    "_" + char.ToLowerInvariant(propName[0]) + propName[1..],
                    "m_" + propName,
                };
                foreach (var candidate in candidates)
                {
                    if (allFieldNames.Contains(candidate))
                    {
                        propToField[propName] = candidate;
                        break;
                    }
                }

                // Final fallback for auto-properties: use camelCase convention
                // (the Java converter generates a field with this name)
                if (!propToField.ContainsKey(propName))
                {
                    propToField[propName] = char.ToLowerInvariant(propName[0]) + propName[1..];
                }
            }

            // For auto-properties (no explicit getter body), create explicit backing fields.
            // Auto-properties have compiler-generated backing fields that aren't accessible
            // by name, so PropertyToFieldAssignmentRewriter's converted assignments
            // (result.fieldName = value) would fail with CS1061.
            var autoPropsToCreate = new List<(string PropName, string FieldName, TypeSyntax Type)>();
            foreach (var (propName, fieldName) in propToField)
            {
                if (allFieldNames.Contains(fieldName))
                    continue;

                var originalProp = node.Members.OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == propName);
                var getter = originalProp?.AccessorList?.Accessors
                    .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));

                if (getter != null && getter.Body == null && getter.ExpressionBody == null)
                {
                    autoPropsToCreate.Add((propName, fieldName, originalProp!.Type));
                }
            }

            if (autoPropsToCreate.Count > 0)
            {
                var newMembers = new SyntaxList<MemberDeclarationSyntax>();
                foreach (var member in rewritten.Members)
                {
                    if (member is PropertyDeclarationSyntax prop &&
                        autoPropsToCreate.Any(a => a.PropName == prop.Identifier.Text))
                    {
                        var info = autoPropsToCreate.First(a => a.PropName == prop.Identifier.Text);

                        // Create: private TypeName fieldName;
                        var backingField = SyntaxFactory.FieldDeclaration(
                            SyntaxFactory.VariableDeclaration(info.Type)
                                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(info.FieldName))))
                            .AddModifiers(
                                SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
                                    .WithTrailingTrivia(SyntaxFactory.Space))
                            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                        newMembers = newMembers.Add(backingField);

                        // Update getter: get => fieldName;
                        var newGetter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                                SyntaxFactory.IdentifierName(info.FieldName)))
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                        var newProp = prop.WithAccessorList(
                            SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(newGetter)));
                        newMembers = newMembers.Add(newProp);
                    }
                    else
                    {
                        newMembers = newMembers.Add(member);
                    }
                }
                rewritten = rewritten.WithMembers(newMembers);
            }

            if (propToField.Count > 0)
            {
                var fieldAssignRewriter = new PropertyToFieldAssignmentRewriter(propToField);
                rewritten = (StructDeclarationSyntax)fieldAssignRewriter.Visit(rewritten)!;
            }
        }

        // NOTE: Do NOT add 'readonly' keyword for L5 method migration.
        // The Java converter would mark fields as final, but migrated methods
        // need to modify the cloned copy's fields (result._field = ...).
        return rewritten;
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            => SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
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
