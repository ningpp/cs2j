using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 结构体转换器 - 将 C# struct 转换为 Java 类
/// </summary>
public class StructTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not StructDeclarationSyntax structDecl)
        {
            throw new ArgumentException($"Expected StructDeclarationSyntax, got {node.GetType()}");
        }

        // Detect readonly and ref modifiers for special handling
        bool isReadOnly = structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        bool isRefStruct = structDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));

        // C# struct 转换为 Java 类
        var javaClass = new JavaClassDeclaration
        {
            Name = structDecl.Identifier.Text,
            Modifiers = ConvertModifiers(structDecl.Modifiers)
        };
        var structSymbol = context.SemanticModel?.GetDeclaredSymbol(structDecl);
        var convertedComments = context.GetDeclarationComments(structDecl, structSymbol).ToCombinedComment();

        // Fix 3: ref struct — emit a leading comment since Java has no stack-only equivalent.
        var leadingCommentLines = new List<string>();
        if (isRefStruct)
        {
            leadingCommentLines.Add("// NOTE: Originally a C# ref struct (stack-only). Java does not enforce stack allocation.");
        }

        // Fix 1: class-level Javadoc warning about value semantics.
        leadingCommentLines.Add("/**");
        leadingCommentLines.Add(" * NOTE: Converted from a C# struct (value type). In C#, struct assignment copies");
        leadingCommentLines.Add(" * the value; in Java, assignment copies the reference.");
        leadingCommentLines.Add(" * Use {@link #clone()} to manually copy instances when value semantics are required.");
        leadingCommentLines.Add(" */");
        javaClass.LeadingComment = ConvertedCommentSet.JoinComments(convertedComments, string.Join("\n", leadingCommentLines));

        // 处理类型参数
        foreach (var typeParam in structDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // Propagate generic type parameter constraints
        ClassTransformer.ApplyTypeParameterConstraints(structDecl.ConstraintClauses, javaClass.TypeParameters, context);

        // 处理接口实现
        if (structDecl.BaseList != null)
        {
            foreach (var baseType in structDecl.BaseList.Types)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (typeInfo.HasValue && typeInfo.Value.Type?.TypeKind == TypeKind.Interface)
                {
                    var iface = typeInfo.Value.Type;
                    // Skip MarshalByRefObject - it doesn't exist in Java
                    if (iface.Name != "MarshalByRefObject" && iface.ToDisplayString() != "System.MarshalByRefObject")
                    {
                        // Skip IEquatable<T> — Java has no equivalent interface; the Equals(T)
                        // method is preserved as a normal public method.
                        if (iface.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>")
                            continue;

                        javaClass.ImplementedTypes.Add(context.MapType(iface));
                    }
                }
            }
        }

        // 处理成员
        context.EnterType(javaClass);
        foreach (var member in structDecl.Members)
        {
            ProcessStructMember(member, javaClass, context);
        }
        context.LeaveType();

        // Fix 2: readonly struct → apply final to each instance field, not to the class declaration.
        if (isReadOnly)
        {
            foreach (var field in javaClass.Fields)
            {
                // Only apply to instance fields; static fields already carry their own modifiers.
                if ((field.Modifiers & JavaModifiers.Static) == 0)
                {
                    field.Modifiers |= JavaModifiers.Final;
                }
            }
        }

        // For non-readonly structs, still apply final to individually-declared readonly fields.
        // The StructTransformer handles the no-arg ctor initialization, so final is safe here.
        if (!isReadOnly)
        {
            var readonlyFieldNames = new HashSet<string>();
            foreach (var member in structDecl.Members)
            {
                if (member is FieldDeclarationSyntax fieldDecl
                    && fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                {
                    foreach (var v in fieldDecl.Declaration.Variables)
                        readonlyFieldNames.Add(ConversionContext.EscapeJavaKeyword(v.Identifier.Text));
                }
            }
            foreach (var field in javaClass.Fields)
            {
                if (readonlyFieldNames.Contains(field.Name))
                    field.Modifiers |= JavaModifiers.Final;
            }
        }

        // Ensure the class implements Cloneable so Object.clone() works correctly
        // when accessed through a reference typed as Object.
        if (!javaClass.ImplementedTypes.Contains("Cloneable"))
            javaClass.ImplementedTypes.Add("Cloneable");

        // Fix 1: Emit a clone() method to approximate C# value-type copy semantics.
        AddCloneMethod(javaClass, isReadOnly, structDecl, context);

        // Fix 6: If an equals() method was generated but hashCode() is absent, emit a hashCode().
        AddHashCodeIfMissing(javaClass, context);

        // Generate equals(Object) override when operator== was converted to valueEquals
        // but no explicit equals(Object) override exists.
        AddEqualsOverrideIfMissing(javaClass, context);

        // Generate toString() for better debugging when no explicit override exists.
        AddToStringIfMissing(javaClass);

        // When struct has comparison operators (lessThan/greaterThan), add Comparable<T>
        // bridge method if body references .compareTo(), mirroring ClassTransformer behavior.
        AddComparableBridgeMethods(javaClass);

        // Fix 4: C# structs always have an implicit zero-arg constructor. Emit one for Java
        // when there are explicit parameterised constructors but no no-arg constructor.
        bool hasExplicitCtors = javaClass.Constructors.Any(c => c.Parameters.Count > 0);
        bool hasNoArgCtor = javaClass.Constructors.Any(c => c.Parameters.Count == 0);
        if (hasExplicitCtors && !hasNoArgCtor)
        {
            var bodyLines = new List<string>();
            foreach (var field in javaClass.Fields)
            {
                bool isInstance = (field.Modifiers & JavaModifiers.Static) == 0;
                bool isFinal = (field.Modifiers & JavaModifiers.Final) != 0;
                bool hasInitializer = !string.IsNullOrWhiteSpace(field.Initializer);
                if (isInstance && isFinal && !hasInitializer)
                {
                    bodyLines.Add($"this.{field.Name} = {GetJavaDefaultValue(field.Type)};");
                }
            }

            if (bodyLines.Count == 0)
            {
                bodyLines.Add("/* zero-initialized */");
            }

            var defaultCtor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = string.Join("\n", bodyLines)
            };
            javaClass.Constructors.Insert(0, defaultCtor);
        }

        return javaClass;
    }

    /// <summary>
    /// Fix 1 — Emit a clone() method to approximate C# struct value-copy semantics.
    /// For non-readonly structs, copies each instance field into a new instance.
    /// For readonly structs (all instance fields are final), only returns new StructName()
    /// because final fields cannot be reassigned after construction.
    /// </summary>
    private static void AddCloneMethod(JavaClassDeclaration javaClass, bool isReadOnly,
        StructDeclarationSyntax structDecl, ConversionContext context)
    {
        // Skip if the C# struct already defined a Clone() method (mapped to clone()).
        if (javaClass.Methods.Any(m => m.Name == "clone"))
            return;

        var instanceFields = javaClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();
        bool hasFinalInstanceField = instanceFields.Any(f => (f.Modifiers & JavaModifiers.Final) != 0);

        // Build a set of field names whose C# type is a user-defined struct.
        // These fields need .clone() in the copy to preserve value semantics (deep copy).
        var structFieldNames = BuildStructFieldNames(structDecl, context);

        // Compute type name including generic type parameters for use in clone body.
        string typeName = javaClass.TypeParameters.Count > 0
            ? $"{javaClass.Name}<{string.Join(", ", javaClass.TypeParameters.Select(tp => tp.Name))}>"
            : javaClass.Name;
        // For 'new' expressions with generics, use diamond operator
        string newTypeName = javaClass.TypeParameters.Count > 0
            ? $"{javaClass.Name}<>"
            : javaClass.Name;

        string cloneBody;
        if (instanceFields.Count == 0)
        {
            cloneBody = $"return new {newTypeName}();";
        }
        else if (isReadOnly)
        {
            // readonly struct: all instance fields are final and cannot be assigned after construction.
            if (TryBuildCtorCopy(javaClass, instanceFields, structFieldNames, out var ctorCopyExpr))
            {
                cloneBody = $"return {ctorCopyExpr};";
            }
            else
            {
                cloneBody = $"// NOTE: readonly struct without matching constructor for field-wise copy.\n" +
                            "return this;";
            }
        }
        else if (hasFinalInstanceField)
        {
            // Some structs can have readonly fields even without the readonly struct modifier.
            // Avoid illegal writes to final fields in clone().
            if (TryBuildCtorCopy(javaClass, instanceFields, structFieldNames, out var ctorCopyExpr))
            {
                cloneBody = $"return {ctorCopyExpr};";
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{typeName} copy = new {newTypeName}();");
                foreach (var field in instanceFields)
                {
                    if ((field.Modifiers & JavaModifiers.Final) == 0)
                    {
                        sb.AppendLine(BuildFieldCopyLine(field, structFieldNames));
                    }
                }
                sb.Append("return copy;");
                cloneBody = sb.ToString();
            }
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{typeName} copy = new {newTypeName}();");
            foreach (var field in instanceFields)
            {
                sb.AppendLine(BuildFieldCopyLine(field, structFieldNames));
            }
            sb.Append("return copy;");
            cloneBody = sb.ToString();
        }

        var cloneMethod = new JavaMethodDeclaration
        {
            Name = "clone",
            ReturnType = typeName,
            Modifiers = JavaModifiers.Public,
            Body = cloneBody,
            LeadingComment = "/** Returns a copy of this struct, approximating C# value-type copy semantics. */"
        };
        javaClass.Methods.Add(cloneMethod);
    }

    /// <summary>
    /// Builds a line for field copy in clone(). Generates .clone() for struct-typed fields.
    /// </summary>
    private static string BuildFieldCopyLine(JavaFieldDeclaration field, HashSet<string> structFieldNames)
    {
        if (structFieldNames.Contains(field.Name))
            return $"copy.{field.Name} = this.{field.Name} != null ? this.{field.Name}.clone() : null;";
        return $"copy.{field.Name} = this.{field.Name};";
    }

    /// <summary>
    /// Builds a set of Java field names whose corresponding C# type is a user-defined struct.
    /// Checks both explicit fields and auto-property backing fields.
    /// </summary>
    private static HashSet<string> BuildStructFieldNames(StructDeclarationSyntax structDecl, ConversionContext context)
    {
        var result = new HashSet<string>();
        if (context.SemanticModel == null)
            return result;

        foreach (var member in structDecl.Members)
        {
            if (member is FieldDeclarationSyntax fieldDecl)
            {
                var fieldType = context.SemanticModel.GetTypeInfo(fieldDecl.Declaration.Type).Type;
                if (StructCloneHelper.IsUserDefinedStruct(fieldType))
                {
                    foreach (var variable in fieldDecl.Declaration.Variables)
                        result.Add(ConversionContext.EscapeJavaKeyword(variable.Identifier.Text));
                }
            }
            else if (member is PropertyDeclarationSyntax propDecl)
            {
                // Auto-properties generate backing fields with camelCase names
                var propSymbol = context.SemanticModel.GetDeclaredSymbol(propDecl);
                if (propSymbol != null && StructCloneHelper.IsUserDefinedStruct(propSymbol.Type))
                {
                    var propName = ConversionContext.EscapeJavaKeyword(propDecl.Identifier.Text);
                    var fieldName = ConversionContext.EscapeJavaKeyword(ToCamelCase(propName));
                    result.Add(fieldName);
                }
            }
        }

        return result;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static bool TryBuildCtorCopy(
        JavaClassDeclaration javaClass,
        IReadOnlyList<JavaFieldDeclaration> instanceFields,
        HashSet<string> structFieldNames,
        out string ctorCopyExpression)
    {
        foreach (var ctor in javaClass.Constructors)
        {
            if (ctor.Parameters.Count != instanceFields.Count)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < ctor.Parameters.Count; i++)
            {
                var param = ctor.Parameters[i];
                var field = instanceFields[i];
                if (!string.Equals(param.Type, field.Type, StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }

                if (!NamesEquivalent(param.Name, field.Name))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            string typeName = javaClass.TypeParameters.Count > 0
                ? $"{javaClass.Name}<{string.Join(", ", javaClass.TypeParameters.Select(tp => tp.Name))}>"
                : javaClass.Name;
            var args = instanceFields.Select(f =>
                structFieldNames.Contains(f.Name)
                    ? $"this.{f.Name} != null ? this.{f.Name}.clone() : null"
                    : $"this.{f.Name}");
            ctorCopyExpression = $"new {typeName}({string.Join(", ", args)})";
            return true;
        }

        ctorCopyExpression = string.Empty;
        return false;
    }

    private static bool NamesEquivalent(string a, string b)
    {
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    }

    private static string GetJavaDefaultValue(string javaType)
    {
        string t = javaType.Trim();
        return t switch
        {
            "boolean" => "false",
            "byte" => "(byte)0",
            "short" => "(short)0",
            "int" => "0",
            "long" => "0L",
            "float" => "0.0f",
            "double" => "0.0d",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    /// <summary>
    /// Fix 6 — When an equals() method was generated for operator== but no hashCode() is present,
    /// emit a hashCode() using Objects.hash() over all instance fields to maintain the
    /// equals/hashCode contract required by Java.
    /// </summary>
    private static void AddHashCodeIfMissing(JavaClassDeclaration javaClass, ConversionContext context)
    {
        // Trigger on either an instance equals() override or a static valueEquals() generated
        // from C# operator==. Both indicate equality comparison semantics that require hashCode.
        bool hasEquals = javaClass.Methods.Any(m => m.Name == "equals" ||
            (m.Name == "valueEquals" && (m.Modifiers & JavaModifiers.Static) != 0));
        bool hasHashCode = javaClass.Methods.Any(m => m.Name == "hashCode");

        if (!hasEquals || hasHashCode)
            return;

        var instanceFields = javaClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();

        string hashBody;
        if (instanceFields.Count == 0)
        {
            hashBody = "return getClass().hashCode();";
        }
        else
        {
            hashBody = $"return Objects.hash({string.Join(", ", instanceFields.Select(f => f.Name))});";
            context.AddImport("java.util.Objects");
        }

        var hashCodeMethod = new JavaMethodDeclaration
        {
            Name = "hashCode",
            ReturnType = "int",
            Modifiers = JavaModifiers.Public,
            Body = hashBody
        };
        hashCodeMethod.Annotations.Add(new JavaAnnotation("Override"));
        javaClass.Methods.Add(hashCodeMethod);
    }

    /// <summary>
    /// When operator== was converted to a static valueEquals() method but no explicit
    /// equals(Object) override exists, generate one that delegates to valueEquals().
    /// This is required for Java collections and equality checks to work correctly.
    /// </summary>
    private static void AddEqualsOverrideIfMissing(JavaClassDeclaration javaClass, ConversionContext context)
    {
        bool hasValueEquals = javaClass.Methods.Any(m =>
            m.Name == "valueEquals" && (m.Modifiers & JavaModifiers.Static) != 0);
        if (!hasValueEquals)
            return;

        bool hasEqualsObject = javaClass.Methods.Any(m =>
            m.Name == "equals" && m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
        if (hasEqualsObject)
            return;

        string className = javaClass.Name;
        var equalsMethod = new JavaMethodDeclaration
        {
            Name = "equals",
            ReturnType = "boolean",
            Modifiers = JavaModifiers.Public,
            Body = $"if (this == o) return true;\n" +
                   $"        if (!(o instanceof {className} other)) return false;\n" +
                   $"        return {className}.valueEquals(this, other);"
        };
        equalsMethod.Parameters.Add(new JavaParameter("Object", "o"));
        equalsMethod.Annotations.Add(new JavaAnnotation("Override"));
        javaClass.Methods.Add(equalsMethod);
        context.AddImport("java.util.Objects");
    }

    /// <summary>
    /// Generate a toString() method for structs when no explicit override exists.
    /// Includes all instance field values for easier debugging.
    /// </summary>
    private static void AddToStringIfMissing(JavaClassDeclaration javaClass)
    {
        bool hasToString = javaClass.Methods.Any(m => m.Name == "toString" && m.Parameters.Count == 0);
        if (hasToString)
            return;

        var instanceFields = javaClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();

        string className = javaClass.Name;
        string body;
        if (instanceFields.Count == 0)
        {
            body = $"return \"{className}{{}}\";";
        }
        else
        {
            var fieldParts = string.Join(" + \", \" + ",
                instanceFields.Select(f => $"\"{f.Name}=\" + {f.Name}"));
            body = $"return \"{className}{{\" + {fieldParts} + \"}}\";";
        }

        var toStringMethod = new JavaMethodDeclaration
        {
            Name = "toString",
            ReturnType = "String",
            Modifiers = JavaModifiers.Public,
            Body = body
        };
        toStringMethod.Annotations.Add(new JavaAnnotation("Override"));
        javaClass.Methods.Add(toStringMethod);
    }

    /// <summary>
    /// When struct has comparison operators (lessThan/greaterThan) and a method body
    /// references .compareTo(), synthesize a compareTo() method and add Comparable&lt;T&gt;.
    /// Mirrors ClassTransformer.AddComparableBridgeMethods logic.
    /// </summary>
    private static void AddComparableBridgeMethods(JavaClassDeclaration javaClass)
    {
        bool hasCompareTo = javaClass.Methods.Any(m => m.Name == "compareTo" && m.Parameters.Count == 1);
        if (hasCompareTo) return;

        bool bodyCallsCompareTo = javaClass.Methods.Any(m =>
            m.Body != null && m.Body.Contains(".compareTo("));
        if (!bodyCallsCompareTo) return;

        var lessThanMethod = javaClass.Methods.FirstOrDefault(m =>
            m.Name == "lessThan"
            && m.Parameters.Count == 2
            && (m.Modifiers & JavaModifiers.Static) != 0
            && m.Parameters[0].Type == javaClass.Name);
        if (lessThanMethod == null) return;

        string className = javaClass.Name;
        string elemType = lessThanMethod.Parameters[0].Type;

        if (!javaClass.ImplementedTypes.Any(t => t == "Comparable" || t.StartsWith("Comparable<")))
            javaClass.ImplementedTypes.Add($"Comparable<{className}>");

        var compareTo = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "compareTo",
            Body = $"if ({className}.lessThan(this, other)) return -1;\n        if ({className}.lessThan(other, this)) return 1;\n        return 0;"
        };
        compareTo.Parameters.Add(new JavaParameter(elemType, "other"));
        javaClass.Methods.Add(compareTo);
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                // Fix 2: Do NOT map ReadOnlyKeyword to Final on the class declaration.
                // For readonly structs, Final is applied to each individual instance field instead.
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // Default to public when no explicit access modifier (C# default = internal)
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;

        return result;
    }

    private void ProcessStructMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = new Transformers.Member.FieldTransformer();
                foreach (var javaField in fieldTransformer.TransformAll(fieldDecl, context))
                {
                    // struct 字段默认是 public（但不加 final，因为 struct 的属性可能有 setter）
                    if (javaField.Modifiers == JavaModifiers.None)
                    {
                        javaField.Modifiers = JavaModifiers.Public;
                    }
                    javaClass.Fields.Add(javaField);

                    // Drain any pre-statements produced during field initializer transformation
                    // (e.g. from object initializers like `new Foo { X = 1 }`).
                    // For static fields, emit them as a static initializer block.
                    DrainFieldPreStatements(javaField, javaClass, context);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                // Skip explicit interface implementations - Java doesn't need them since the
                // public member already satisfies the interface requirement
                if (propDecl.ExplicitInterfaceSpecifier != null) break;
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection collection)
                {
                    foreach (var prop in collection.Members)
                    {
                        if (prop is JavaFieldDeclaration jf) javaClass.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm);
                    }
                }
                else if (props is JavaFieldDeclaration jf)
                {
                    javaClass.Fields.Add(jf);
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                // Explicit interface implementations are generated as public methods (not skipped)
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, javaMethod);
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = factory.CreateOperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, opMethod);
                break;

            case ConversionOperatorDeclarationSyntax convDecl:
                var convOpTransformer = factory.CreateOperatorTransformer();
                var convOpMethod = convOpTransformer.TransformConversion(convDecl, context);
                if (convOpMethod != null)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, convOpMethod);
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerResult = indexerTransformer.Transform(indexerDecl, context);
                if (indexerResult is JavaMemberCollection indexerCollection)
                {
                    foreach (var indexerMember in indexerCollection.Members)
                    {
                        if (indexerMember is JavaMethodDeclaration jm) ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm);
                    }
                }
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    ClassTransformer.AddCtorIfNotDuplicateInternal(javaClass, javaCtor);
                }
                else if (ctor is JavaStaticInitializerBlock staticInitBlock)
                {
                    javaClass.StaticInitializers.Add(staticInitBlock);
                }
                break;

            // ── Nested type declarations (mirroring ClassTransformer.ProcessMember) ──

            case StructDeclarationSyntax nestedStruct:
                var nestedStructTransformer = new StructTransformer();
                var nestedStructResult = nestedStructTransformer.Transform(nestedStruct, context);
                if (nestedStructResult is JavaClassDeclaration jcs)
                {
                    jcs.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(jcs);
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                var nestedClassTransformer = factory.CreateClassTransformer();
                var nestedClassResult = nestedClassTransformer.Transform(nestedClass, context);
                if (nestedClassResult is JavaClassDeclaration jcn)
                {
                    jcn.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(jcn);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                var enumTransformer = new Transformers.Type.EnumTransformer();
                var nestedEnumResult = enumTransformer.TransformEnum(nestedEnum, context);
                if (nestedEnumResult is JavaEnumDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(je);
                }
                else if (nestedEnumResult is JavaClassDeclaration jcEnum)
                {
                    // [Flags] enums generate a JavaClassDeclaration (int constants class)
                    jcEnum.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(jcEnum);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var nestedIfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedIfaceResult = nestedIfaceTransformer.Transform(nestedInterface, context);
                if (nestedIfaceResult is JavaInterfaceDeclaration ji)
                {
                    ji.Modifiers |= JavaModifiers.Static;
                    javaClass.NestedTypes.Add(ji);
                }
                break;

            case DelegateDeclarationSyntax nestedDelegate:
                var delegateTransformer = new Transformers.Type.DelegateTransformer();
                var nestedDelegateResult = delegateTransformer.TransformDelegate(nestedDelegate, context);
                if (nestedDelegateResult != null)
                {
                    javaClass.NestedTypes.Add(nestedDelegateResult);
                }
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                var eventFieldTransformer = new Transformers.Member.EventFieldTransformer();
                var eventMembers = eventFieldTransformer.TransformEvent(eventFieldDecl, context);
                foreach (var em in eventMembers)
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name)) javaClass.Fields.Add(ef);
                    if (em is JavaMethodDeclaration emm) ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, emm);
                }
                break;
        }
    }

    /// <summary>
    /// Drains any pre-statements produced during field initializer transformation and
    /// emits them as a static initializer block (for static fields) or inlines them
    /// into the field initializer (for instance fields).
    /// </summary>
    internal static void DrainFieldPreStatementsPublic(JavaFieldDeclaration javaField, JavaClassDeclaration javaClass, ConversionContext context)
        => DrainFieldPreStatements(javaField, javaClass, context);

    private static void DrainFieldPreStatements(JavaFieldDeclaration javaField, JavaClassDeclaration javaClass, ConversionContext context)
    {
        if (!context.HasPendingPreStatements)
            return;

        var preStatements = context.DrainPreStatements();
        if (preStatements.Count == 0)
            return;

        bool isStatic = (javaField.Modifiers & JavaModifiers.Static) != 0;
        if (isStatic)
        {
            // Move initialization to a static initializer block:
            // static {
            //     var _obj1 = new Foo();
            //     _obj1.X = 1;
            //     FieldName = _obj1;
            // }
            var staticBlock = new JavaStaticInitializerBlock();
            foreach (var stmt in preStatements)
            {
                var trimmed = stmt.TrimEnd();
                staticBlock.Statements.Add(trimmed.EndsWith(';') ? trimmed : trimmed + ";");
            }
            // The field initializer holds the temp variable name; assign it to the field.
            if (!string.IsNullOrWhiteSpace(javaField.Initializer))
            {
                staticBlock.Statements.Add($"{javaField.Name} = {javaField.Initializer};");
                javaField.Initializer = null;
            }
            javaClass.StaticInitializers.Add(staticBlock);
        }
        else
        {
            // For instance fields, the simplest approach is to build the full initialization inline.
            // We concatenate pre-statements and the final value into a comment-documented block.
            // Since Java doesn't support multi-statement field initializers, we note this as a TODO.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("/* Object initializer — consider moving to constructor. */");
            foreach (var stmt in preStatements)
                sb.AppendLine(stmt.TrimEnd());
            if (!string.IsNullOrWhiteSpace(javaField.Initializer))
                sb.Append(javaField.Initializer);
            javaField.Initializer = sb.ToString().Trim();
        }
    }
}
