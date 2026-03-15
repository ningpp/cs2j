using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
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
        javaClass.LeadingComment = string.Join("\n", leadingCommentLines);

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

        // Fix 1: Emit a clone() method to approximate C# value-type copy semantics.
        AddCloneMethod(javaClass, isReadOnly);

        // Fix 6: If an equals() method was generated but hashCode() is absent, emit a hashCode().
        AddHashCodeIfMissing(javaClass, context);

        // Fix 4: C# structs always have an implicit zero-arg constructor. Emit one for Java
        // when there are explicit parameterised constructors but no no-arg constructor.
        bool hasExplicitCtors = javaClass.Constructors.Any(c => c.Parameters.Count > 0);
        bool hasNoArgCtor = javaClass.Constructors.Any(c => c.Parameters.Count == 0);
        if (hasExplicitCtors && !hasNoArgCtor)
        {
            var defaultCtor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = "/* zero-initialized */"
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
    private static void AddCloneMethod(JavaClassDeclaration javaClass, bool isReadOnly)
    {
        var instanceFields = javaClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();

        string cloneBody;
        if (isReadOnly || instanceFields.Count == 0)
        {
            // readonly struct: all instance fields are final and cannot be assigned after construction.
            cloneBody = $"// NOTE: readonly struct — final fields cannot be reassigned after construction.\n" +
                        $"return new {javaClass.Name}();";
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{javaClass.Name} copy = new {javaClass.Name}();");
            foreach (var field in instanceFields)
            {
                sb.AppendLine($"copy.{field.Name} = this.{field.Name};");
            }
            sb.Append("return copy;");
            cloneBody = sb.ToString();
        }

        string returnType = javaClass.TypeParameters.Count > 0
            ? $"{javaClass.Name}<{string.Join(", ", javaClass.TypeParameters.Select(tp => tp.Name))}>"
            : javaClass.Name;

        var cloneMethod = new JavaMethodDeclaration
        {
            Name = "clone",
            ReturnType = returnType,
            Modifiers = JavaModifiers.Public,
            Body = cloneBody,
            LeadingComment = "/** Returns a copy of this struct, approximating C# value-type copy semantics. */"
        };
        javaClass.Methods.Add(cloneMethod);
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

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    ClassTransformer.AddCtorIfNotDuplicateInternal(javaClass, javaCtor);
                }
                break;
        }
    }
}
