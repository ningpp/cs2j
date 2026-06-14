using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 记录 (record) 转换器
/// </summary>
public class RecordTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not RecordDeclarationSyntax recordDecl)
        {
            throw new ArgumentException($"Expected RecordDeclarationSyntax, got {node.GetType()}");
        }

        // Fix 4: record struct has value semantics — route to mutable class path
        bool isRecordStruct = recordDecl.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword);
        if (isRecordStruct)
        {
            return CreateMutableRecordStruct(recordDecl, context);
        }

        // Abstract records cannot be Java records (Java records are implicitly final).
        // Fall back to abstract class which preserves the inheritance semantics.
        bool isAbstract = recordDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        if (isAbstract)
        {
            return CreateImmutableClass(recordDecl, context);
        }

        // 如果目标 Java 版本支持 record (Java 14+)，使用 record
        var useRecord = context.Options.UseRecords &&
                       context.Options.TargetJavaVersion >= JavaVersion.Java25;

        if (useRecord)
        {
            return CreateJavaRecord(recordDecl, context);
        }
        else
        {
            // 否则创建等效的不可变类
            return CreateImmutableClass(recordDecl, context);
        }
    }

    private JavaTypeDeclaration CreateJavaRecord(RecordDeclarationSyntax recordDecl, ConversionContext context)
    {
        var modifiers = ConvertModifiers(recordDecl.Modifiers);
        // Java records are implicitly final — strip redundant Final flag from sealed keyword
        modifiers &= ~JavaModifiers.Final;

        var javaRecord = new JavaClassDeclaration
        {
            Name = recordDecl.Identifier.Text,
            IsRecord = true,
            Modifiers = modifiers
        };
        var recordSymbol = context.GetDeclaredSymbol(recordDecl);
        javaRecord.LeadingComment = context.GetDeclarationComments(recordDecl, recordSymbol).ToCombinedComment();

        // Populate positional record component list so Java record header includes (Type name, ...)
        var positionalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in recordDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.GetTypeInfo(param.Type!);
            var javaType = typeInfo.Type != null
                ? context.MapType(typeInfo.Type)
                : "Object";
            var componentName = JavaNaming.EscapeJavaKeyword(ToCamelCase(param.Identifier.Text));
            javaRecord.RecordComponents.Add(new JavaRecordComponent(javaType, componentName));
            positionalNames.Add(param.Identifier.Text);
            positionalNames.Add(componentName);
        }

        // 处理基类/接口
        if (recordDecl.BaseList != null)
        {
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                var typeInfo = context.GetTypeInfo(baseType.Type);
                if (typeInfo.Type != null)
                {
                    if (typeInfo.Type.TypeKind == TypeKind.Interface)
                    {
                        javaRecord.ImplementedTypes.Add(context.MapType(typeInfo.Type));
                    }
                    else if (typeInfo.Type.TypeKind == TypeKind.Class
                             && typeInfo.Type.SpecialType != SpecialType.System_Object)
                    {
                        javaRecord.ExtendedType = context.MapType(typeInfo.Type);
                    }
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in recordDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaRecord.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // 处理附加成员
        foreach (var member in recordDecl.Members)
        {
            ProcessRecordMember(member, javaRecord, context, positionalNames);
        }

        return javaRecord;
    }

    private JavaTypeDeclaration CreateImmutableClass(RecordDeclarationSyntax recordDecl, ConversionContext context)
    {
        var classModifiers = ConvertModifiers(recordDecl.Modifiers);
        // Non-abstract records are implicitly sealed in C# → final in Java
        if ((classModifiers & JavaModifiers.Abstract) == 0)
            classModifiers |= JavaModifiers.Final;

        var javaClass = new JavaClassDeclaration
        {
            Name = recordDecl.Identifier.Text,
            Modifiers = classModifiers
        };
        var recordSymbol = context.GetDeclaredSymbol(recordDecl);
        javaClass.LeadingComment = context.GetDeclarationComments(recordDecl, recordSymbol).ToCombinedComment();

        // 为每个位置参数创建字段和构造函数
        var fields = new List<JavaFieldDeclaration>();
        var constructorParams = new List<JavaParameter>();

        foreach (var param in recordDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.GetTypeInfo(param.Type!);
            var javaType = typeInfo.Type != null ? context.MapType(typeInfo.Type) : "Object";
            var paramName = param.Identifier.Text;
            var fieldName = JavaNaming.EscapeJavaKeyword(ToCamelCase(paramName));

            // 创建 final 字段
            fields.Add(new JavaFieldDeclaration
            {
                Type = javaType,
                Name = fieldName,
                Modifiers = JavaModifiers.Private | JavaModifiers.Final
            });

            constructorParams.Add(new JavaParameter(javaType, fieldName));
        }

        javaClass.Fields.AddRange(fields);

        // Collect base constructor arguments from the record's primary constructor base type
        // e.g. record Derived(int A) : Base(A) → super(a) in constructor
        ArgumentListSyntax? baseCtorArgs = null;
        if (recordDecl.BaseList != null)
        {
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                var typeInfo = context.GetTypeInfo(baseType.Type);
                if (typeInfo.Type != null)
                {
                    if (typeInfo.Type.TypeKind == TypeKind.Interface)
                    {
                        javaClass.ImplementedTypes.Add(context.MapType(typeInfo.Type));
                    }
                    else if (typeInfo.Type.TypeKind == TypeKind.Class
                             && typeInfo.Type.SpecialType != SpecialType.System_Object)
                    {
                        javaClass.ExtendedType = context.MapType(typeInfo.Type);
                    }
                }

                if (baseType is PrimaryConstructorBaseTypeSyntax primaryBase &&
                    primaryBase.ArgumentList?.Arguments.Count > 0)
                {
                    baseCtorArgs = primaryBase.ArgumentList;
                }
            }
        }

        // 创建构造函数
        if (constructorParams.Count > 0 || baseCtorArgs != null)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = GenerateConstructorBody(fields.Select(f => f.Name).ToList())
            };
            // 添加参数到只读集合
            foreach (var param in constructorParams)
            {
                ctor.Parameters.Add(param);
            }
            // Fix 3: emit super(...) when base record constructor takes arguments
            if (baseCtorArgs != null)
            {
                var exprTransformer = ExpressionTransformerFacade.Instance;
                var argList = string.Join(", ",
                    baseCtorArgs.Arguments.Select(a => exprTransformer.Transform(a.Expression, context)));
                ctor.Initializer = $"super({argList})";
            }
            javaClass.Constructors.Add(ctor);
        }

        // 为每个字段生成 getter
        foreach (var field in fields)
        {
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = "get" + char.ToUpper(field.Name[0]) + field.Name.Substring(1),
                ReturnType = field.Type,
                Modifiers = JavaModifiers.Public,
                Body = $"return {field.Name};",
                IsBodyExpression = true
            });
        }

        // 生成 equals, hashCode, toString
        GenerateObjectMethods(javaClass, context);

        return javaClass;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        bool hasAccessModifier = false;

        foreach (var modifier in modifiers)
        {
            // 使用 RawKind 来避免命名空间冲突
            var kind = (Microsoft.CodeAnalysis.CSharp.SyntaxKind)modifier.RawKind;
            result |= kind switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SealedKeyword => JavaModifiers.Final,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
            if (kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword)
                hasAccessModifier = true;
        }

        // Default to public when no explicit access modifier (C# default = internal)
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;

        return result;
    }

    private string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLower(name[0]) + name.Substring(1);
    }

    private string GenerateConstructorBody(List<string> fieldNames)
    {
        var assignments = string.Join("\n        ", fieldNames.Select(name => $"this.{name} = {name};"));
        return assignments;
    }

    private void GenerateObjectMethods(JavaClassDeclaration javaClass, ConversionContext context)
    {
        var fields = javaClass.Fields;
        var fieldNames = fields.Select(f => f.Name).ToList();
        var className = javaClass.Name;

        // equals — Fix 2: use Arrays.equals for array-typed fields
        string equalsBody;
        if (fieldNames.Count == 0)
        {
            equalsBody = $"if (this == o) return true;\nreturn o instanceof {className};";
        }
        else
        {
            context.AddImport("java.util.Objects");
            var comparisons = string.Join(" &&\n           ", fields.Select(f =>
            {
                bool isDeepArray = f.Type.Contains("[][]");
                bool isArray = f.Type.EndsWith("[]");
                return isDeepArray
                    ? $"java.util.Arrays.deepEquals(this.{f.Name}, other.{f.Name})"
                    : isArray
                    ? $"java.util.Arrays.equals(this.{f.Name}, other.{f.Name})"
                    : $"Objects.equals(this.{f.Name}, other.{f.Name})";
            }));
            equalsBody = $"if (this == o) return true;\n" +
                         $"if (!(o instanceof {className})) return false;\n" +
                         $"{className} other = ({className}) o;\n" +
                         $"return {comparisons};";
        }
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "equals",
            ReturnType = "boolean",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Parameters = { new JavaParameter("Object", "o") },
            Body = equalsBody
        });

        // hashCode — Fix 2: use Arrays.hashCode for array-typed fields
        string hashCodeBody;
        bool hasArrayFields = fields.Any(f => f.Type.EndsWith("[]"));
        if (fieldNames.Count == 0)
        {
            context.AddImport("java.util.Objects");
            hashCodeBody = $"return Objects.hash();";
        }
        else if (!hasArrayFields)
        {
            context.AddImport("java.util.Objects");
            hashCodeBody = $"return Objects.hash({string.Join(", ", fieldNames)});";
        }
        else
        {
            // Mixed or all-array fields: build hash manually so Arrays.hashCode can be applied per field
            context.AddImport("java.util.Objects");
            var sb = new System.Text.StringBuilder("int result = 1;\n");
            foreach (var f in fields)
            {
                if (f.Type.Contains("[][]"))
                    sb.Append($"result = 31 * result + java.util.Arrays.deepHashCode({f.Name});\n");
                else if (f.Type.EndsWith("[]"))
                    sb.Append($"result = 31 * result + java.util.Arrays.hashCode({f.Name});\n");
                else
                    sb.Append($"result = 31 * result + Objects.hashCode({f.Name});\n");
            }
            sb.Append("return result;");
            hashCodeBody = sb.ToString();
        }
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "hashCode",
            ReturnType = "int",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Body = hashCodeBody
        });

        // toString
        var toStringBody = fieldNames.Count == 0
            ? $"return \"{className}{{}}\"; "
            : $"return \"{className}{{\" + {string.Join(" + \", \" + ", fieldNames.Select(f => $"\"{f}=\" + {f}"))} + \"}}\"; ";
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "toString",
            ReturnType = "String",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Body = toStringBody
        });
    }

    private static readonly TransformerFactory _factory = new();

    private void ProcessRecordMember(MemberDeclarationSyntax member, JavaClassDeclaration javaRecord, ConversionContext context, HashSet<string> positionalNames)
    {
        switch (member)
        {
            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = _factory.CreateConstructorTransformer();
                var ctorResult = ctorTransformer.Transform(ctorDecl, context);
                if (ctorResult is JavaConstructorDeclaration javaCtor)
                    javaRecord.Constructors.Add(javaCtor);
                else if (ctorResult is JavaStaticInitializerBlock staticBlock)
                    javaRecord.StaticInitializers.Add(staticBlock);
                break;

            case PropertyDeclarationSyntax propDecl:
                // Fix 5: skip properties whose name matches a positional component (getter already auto-generated)
                if (positionalNames.Contains(propDecl.Identifier.Text) ||
                    positionalNames.Contains(ToCamelCase(propDecl.Identifier.Text)))
                    break;
                var propTransformer = _factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection collection)
                {
                    foreach (var prop in collection.Members)
                    {
                        if (prop is JavaFieldDeclaration jf) javaRecord.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) javaRecord.Methods.Add(jm);
                    }
                }
                else if (props is JavaFieldDeclaration jField)
                {
                    javaRecord.Fields.Add(jField);
                }
                else if (props is JavaMethodDeclaration jMethod)
                {
                    javaRecord.Methods.Add(jMethod);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = _factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    javaRecord.Methods.Add(javaMethod);
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = _factory.CreateOperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    javaRecord.Methods.Add(opMethod);
                break;

            case ConversionOperatorDeclarationSyntax convDecl:
                var convOpTransformer = _factory.CreateOperatorTransformer();
                var convOpMethod = convOpTransformer.TransformConversion(convDecl, context);
                if (convOpMethod != null)
                    javaRecord.Methods.Add(convOpMethod);
                break;
        }
    }

    // Record struct has value semantics — produce a mutable Java class (non-final fields, all-args ctor)
    private JavaTypeDeclaration CreateMutableRecordStruct(RecordDeclarationSyntax recordDecl, ConversionContext context)
    {
        var javaClass = new JavaClassDeclaration
        {
            Name = recordDecl.Identifier.Text,
            Modifiers = ConvertModifiers(recordDecl.Modifiers)
        };
        var recordSymbol = context.GetDeclaredSymbol(recordDecl);
        javaClass.LeadingComment = context.GetDeclarationComments(recordDecl, recordSymbol).ToCombinedComment();

        // Handle interface implementations
        if (recordDecl.BaseList != null)
        {
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                var typeInfo = context.GetTypeInfo(baseType.Type);
                if (typeInfo.Type?.TypeKind == TypeKind.Interface)
                {
                    javaClass.ImplementedTypes.Add(context.MapType(typeInfo.Type));
                }
            }
        }

        var constructorParams = new List<JavaParameter>();

        foreach (var param in recordDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.GetTypeInfo(param.Type!);
            var javaType = typeInfo.Type != null
                ? context.MapType(typeInfo.Type)
                : "Object";
            var paramName = JavaNaming.EscapeJavaKeyword(ToCamelCase(param.Identifier.Text));

            // Mutable public fields (no Final modifier) for record struct
            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Type = javaType,
                Name = paramName,
                Modifiers = JavaModifiers.Public
            });

            constructorParams.Add(new JavaParameter(javaType, paramName));
        }

        // All-args constructor + default no-arg constructor for struct semantics
        if (constructorParams.Count > 0)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = GenerateConstructorBody(javaClass.Fields.Select(f => f.Name).ToList())
            };
            foreach (var param in constructorParams)
                ctor.Parameters.Add(param);
            javaClass.Constructors.Add(ctor);

            // Default no-arg constructor (C# record structs always have one)
            var defaultCtor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = GenerateDefaultConstructorBody(javaClass.Fields)
            };
            javaClass.Constructors.Add(defaultCtor);
        }

        // Process extra members (no positional names to skip since fields are public, not as property accessors)
        foreach (var member in recordDecl.Members)
        {
            ProcessRecordMember(member, javaClass, context, new HashSet<string>());
        }

        return javaClass;
    }

    private static string GenerateDefaultConstructorBody(List<JavaFieldDeclaration> fields)
    {
        var assignments = string.Join("\n        ", fields.Select(f =>
        {
            var defaultValue = f.Type switch
            {
                "int" or "long" or "short" or "byte" => "0",
                "float" => "0.0f",
                "double" => "0.0",
                "char" => "'\\0'",
                "boolean" => "false",
                _ => "null"
            };
            return $"this.{f.Name} = {defaultValue};";
        }));
        return assignments;
    }
}
