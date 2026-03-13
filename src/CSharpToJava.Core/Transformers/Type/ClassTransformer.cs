using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using System.Text;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 类转换器 - 将 C# 类转换为 Java 类
/// </summary>
public class ClassTransformer : ITypeTransformer
{
    /// <summary>
    /// 转换已合并的 partial 类型声明
    /// </summary>
    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected merged ClassDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));

        var javaClass = new JavaClassDeclaration
        {
            Name = mergedType.TypeSymbol.Name,
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        // Use the semantic model from the merged type for better type resolution
        var semanticModel = context.GetSemanticModelForTree(classDecl.SyntaxTree);

        // 处理基类 - 使用符号信息
        var baseType = mergedType.TypeSymbol.BaseType;
        if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            // Skip MarshalByRefObject - it doesn't exist in Java
            if (baseType.Name != "MarshalByRefObject" && baseType.ToDisplayString() != "System.MarshalByRefObject")
            {
                javaClass.ExtendedType = context.MapType(baseType);
            }
        }

        // 处理接口 - 使用符号信息
        foreach (var iface in mergedType.TypeSymbol.AllInterfaces)
        {
            // Only add directly implemented interfaces, not inherited ones
            var isDirect = false;
            foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
            {
                // Get the right semantic model for this specific syntax node's tree
                var nodeSemanticModel = context.GetSemanticModelForTree(syntaxNode.SyntaxTree) ?? semanticModel;

                if (syntaxNode.BaseList != null)
                {
                    foreach (var baseTypeSyntax in syntaxNode.BaseList.Types)
                    {
                        ISymbol? symbol = null;
                        try { symbol = nodeSemanticModel?.GetSymbolInfo(baseTypeSyntax.Type).Symbol; } catch { }
                        if (SymbolEqualityComparer.Default.Equals(symbol, iface))
                        {
                            isDirect = true;
                            break;
                        }
                    }
                }
            }

            if (isDirect)
            {
                // Skip MarshalByRefObject - it doesn't exist in Java
                if (iface.Name != "MarshalByRefObject" && iface.ToDisplayString() != "System.MarshalByRefObject")
                {
                    var mappedIface = context.MapType(iface);
                    // ICollection<T> maps to java.util.Collection<T> for type bounds (CollectionUtilities),
                    // but when used as an IMPLEMENTED interface it requires all abstract methods to be
                    // implemented (addAll, retainAll, containsAll, etc). Use Iterable instead since
                    // custom collection classes typically don't implement the full Collection contract.
                    if (iface.Name == "ICollection" && iface.ContainingNamespace?.ToString()?.StartsWith("System") == true)
                        mappedIface = mappedIface.Replace("Collection", "Iterable");
                    javaClass.ImplementedTypes.Add(mappedIface);
                }
            }
        }

        // 处理类型参数 - 使用符号信息
        foreach (var typeParam in mergedType.TypeSymbol.TypeParameters)
        {
            var jtp = new JavaTypeParameter(typeParam.Name);
            // Propagate constraints from symbol
            foreach (var constraintType in typeParam.ConstraintTypes)
            {
                var bound = context.MapType(constraintType);
                if (!string.IsNullOrEmpty(bound) && bound != "Object")
                    jtp.Bounds.Add(bound);
            }
            javaClass.TypeParameters.Add(jtp);
        }

        // Sync type params to the placeholder in context so static method transformer can see them
        foreach (var tp in javaClass.TypeParameters)
            context.CurrentType!.TypeParameters.Add(tp);

        // Process members from original syntax nodes (not the synthetic merged node).
        // Original nodes are from the compilation trees, so semantic model works correctly.
        var seenMemberKeys = new HashSet<string>();
        foreach (var originalNode in mergedType.OriginalSyntaxNodes)
        {
            // Switch to the semantic model for this file
            var nodeModel = context.GetSemanticModelForTree(originalNode.SyntaxTree);
            if (nodeModel != null) context.SemanticModel = nodeModel;

            foreach (var member in originalNode.Members)
            {
                // Deduplication for partial types: use full parameter type signature to preserve overloads
                var key = member switch
                {
                    MethodDeclarationSyntax m => $"m:{m.Identifier.Text}:{string.Join(",", m.ParameterList?.Parameters.Select(p => p.Type?.ToString() ?? "?") ?? Enumerable.Empty<string>())}",
                    PropertyDeclarationSyntax p => $"p:{p.Identifier.Text}",
                    FieldDeclarationSyntax f => $"f:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
                    ConstructorDeclarationSyntax c => $"ctor:{string.Join(",", c.ParameterList?.Parameters.Select(p => p.Type?.ToString() ?? "?") ?? Enumerable.Empty<string>())}",
                    _ => $"other:{member.GetHashCode()}"
                };
                if (seenMemberKeys.Add(key))
                    ProcessMember(member, javaClass, context);
            }
        }

        RemoveCompareToBridgeConflicts(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableSizeBridgeMethods(javaClass);
        AddCloneableBridgeMethods(javaClass);
        context.LeaveType();

        return javaClass;
    }

    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // 检查这是否是已合并的 partial 类型的一部分
        if (context.IsMergedPartialType(node))
        {
            // 跳过处理 - 已合并的类型会通过 TransformMerged 处理
            // 返回一个占位符以避免处理错误
            context.EnterType(CreatePlaceholderClass(node is ClassDeclarationSyntax cls ? cls.Identifier.Text : "Unknown"));
            context.LeaveType();
            return new JavaClassDeclaration { Name = "MergedTypePlaceholder" };
        }

        if (node is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected ClassDeclarationSyntax, got {node.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));

        var javaClass = new JavaClassDeclaration
        {
            Name = GetJavaClassName(classDecl),
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        // 处理基类
        if (classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                if (baseType.Type is SimpleNameSyntax simpleName)
                {
                    var typeName = simpleName.Identifier.Text;
                    if (typeName == "Object" || typeName == "ValueType") continue;

                    // 检查是否是基类（第一个通常是基类，后面是接口）
                    var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                    if (typeInfo.HasValue && typeInfo.Value.Type?.TypeKind == TypeKind.Class)
                    {
                        javaClass.ExtendedType = context.MapType(typeInfo.Value.Type);
                    }
                    else if (typeInfo.HasValue && typeInfo.Value.Type?.TypeKind == TypeKind.Interface)
                    {
                        var ifaceType = typeInfo.Value.Type;
                        var mappedIface = context.MapType(ifaceType);
                        // ICollection<T> as an implemented interface → use Iterable to avoid requiring all abstract Collection methods
                        if (ifaceType is INamedTypeSymbol namedIface &&
                            namedIface.Name == "ICollection" &&
                            namedIface.ContainingNamespace?.ToString()?.StartsWith("System") == true)
                            mappedIface = mappedIface.Replace("Collection", "Iterable");
                        javaClass.ImplementedTypes.Add(mappedIface);
                    }
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in classDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // Propagate generic type parameter constraints (where T : IBound)
        ApplyTypeParameterConstraints(classDecl.ConstraintClauses, javaClass.TypeParameters, context);

        // Sync type params to the placeholder in context so static method transformer can see them
        foreach (var tp in javaClass.TypeParameters)
            context.CurrentType!.TypeParameters.Add(tp);

        // 处理成员
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        RemoveCompareToBridgeConflicts(javaClass);
        AddIteratorBridgeMethods(javaClass);
        AddIterableSizeBridgeMethods(javaClass);
        AddCloneableBridgeMethods(javaClass);
        context.LeaveType();

        return javaClass;
    }

    private string GetJavaClassName(ClassDeclarationSyntax classDecl)
    {
        var name = classDecl.Identifier.Text;

        // 移除 C# 特殊后缀
        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            // partial 类 - 保持原名称
        }

        return name;
    }

    private JavaClassDeclaration CreatePlaceholderClass(string name)
    {
        return new JavaClassDeclaration { Name = name };
    }

    /// <summary>
    /// Reads TypeParameterConstraintClauseSyntax nodes and populates Bounds on the matching JavaTypeParameter.
    /// </summary>
    internal static void ApplyTypeParameterConstraints(
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        List<JavaTypeParameter> typeParameters,
        ConversionContext context)
    {
        foreach (var clause in constraintClauses)
        {
            var paramName = clause.Name.Identifier.Text;
            var jtp = typeParameters.FirstOrDefault(tp => tp.Name == paramName);
            if (jtp == null) continue;

            foreach (var constraint in clause.Constraints)
            {
                if (constraint is TypeConstraintSyntax typeConstraint)
                {
                    var typeInfo = context.SemanticModel?.GetTypeInfo(typeConstraint.Type);
                    if (typeInfo.HasValue && typeInfo.Value.Type != null)
                    {
                        var bound = context.MapType(typeInfo.Value.Type);
                        if (!string.IsNullOrEmpty(bound) && bound != "Object")
                            jtp.Bounds.Add(bound);
                    }
                }
                // ClassConstraint, StructConstraint, ConstructorConstraint have no Java equivalent
            }
        }
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, ConversionContext context)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public, // Java 没有 internal，使用 public
                SyntaxKind.StaticKeyword => JavaModifiers.Final, // C# static class → Java final class (top-level classes can't be static in Java)
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.NewKeyword => JavaModifiers.Override, // new 成员
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.VirtualKeyword => JavaModifiers.None, // Java 默认是 virtual
                SyntaxKind.UnsafeKeyword => JavaModifiers.None, // unsafe 不支持
                _ => JavaModifiers.None
            };
        }

        // In C#, top-level types with no accessibility modifier default to 'internal'.
        // Map this to Java 'public' so the type is accessible across packages.
        // Nested types with no modifier default to 'private' in C# — we still make them
        // 'public' because they may be referenced from other classes in the converted code.
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
        {
            result |= JavaModifiers.Public;
        }

        return result;
    }

    /// <summary>
    /// 计算 Java 类型擦除后的原始类型（去掉泛型参数）
    /// </summary>
    private static string EraseType(string type)
    {
        var idx = type.IndexOf('<');
        return idx >= 0 ? type.Substring(0, idx).TrimEnd() : type;
    }

    private static string ErasedParamSig(IEnumerable<JavaParameter> parameters)
        => string.Join(",", parameters.Select(p => EraseType(p.Type)));

    internal static void AddMethodIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaMethodDeclaration javaMethod)
    {
        var erasedSig = ErasedParamSig(javaMethod.Parameters);
        if (!javaClass.Methods.Any(m => m.Name == javaMethod.Name && ErasedParamSig(m.Parameters) == erasedSig))
            javaClass.Methods.Add(javaMethod);
    }

    internal static void AddCtorIfNotDuplicateInternal(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
    {
        var erasedSig = ErasedParamSig(ctor.Parameters);
        if (!javaClass.Constructors.Any(c => ErasedParamSig(c.Parameters) == erasedSig))
        {
            javaClass.Constructors.Add(ctor);
            return;
        }

        // Type-erasure conflict: two constructors with same erased parameter signature.
        // Convert the conflicting constructor to a static factory method named "createFrom<ParamType>".
        // The factory method body creates an instance via the no-arg constructor + initialization.
        if (ctor.Parameters.Count == 1)
        {
            var paramType = ctor.Parameters[0].Type;
            // Build a factory method name from the parameter type
            var suffix = System.Text.RegularExpressions.Regex.Replace(paramType, @"[<>,\s\[\]?]", "_").Trim('_');
            suffix = System.Text.RegularExpressions.Regex.Replace(suffix, "_+", "_");
            var factoryName = $"createFrom_{suffix}";

            // Avoid duplicate factory methods
            if (javaClass.Methods.Any(m => m.Name == factoryName))
                return;

            var className = javaClass.Name;
            var paramName = ctor.Parameters[0].Name;

            // Extract this() initializer call args from ctor body (they appear as "this(args);")
            string initCall = $"new {className}()";
            string bodyWithoutInit = ctor.Body ?? "";

            // The constructor body includes this() lines as the first statement.
            // Replace them with direct initialization of __inst.
            // Pattern: lines starting with "this(" are initializers.
            var lines = bodyWithoutInit.Split('\n').ToList();
            var filteredLines = new System.Text.StringBuilder();
            bool foundInit = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!foundInit && trimmed.StartsWith("this(") && trimmed.EndsWith(");"))
                {
                    // Convert "this(args);" → "__inst = new ClassName(args);"
                    var innerArgs = trimmed.Substring(5, trimmed.Length - 7); // strip "this(" and ");"
                    filteredLines.AppendLine($"        Rectangle __inst = new {className}({innerArgs});");
                    foundInit = true;
                }
                else
                {
                    // Replace bare instance method calls (without explicit receiver) with __inst. prefix
                    // This handles "add(r);" → "__inst.add(r);"
                    var rewritten = System.Text.RegularExpressions.Regex.Replace(
                        line, @"\b([a-z][a-zA-Z0-9]*)\(", m => {
                            var methodName = m.Groups[1].Value;
                            // Skip Java keywords and common static methods
                            if (methodName is "for" or "if" or "while" or "return" or "new" or "super" or "this")
                                return m.Value;
                            return $"__inst.{m.Value}";
                        });
                    filteredLines.AppendLine(rewritten);
                }
            }

            if (!foundInit)
            {
                // No this() initializer — prepend __inst creation
                var prefix = $"        {className} __inst = new {className}();\n";
                filteredLines.Insert(0, prefix);
            }

            filteredLines.AppendLine($"        return __inst;");

            var factory = new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                ReturnType = className,
                Name = factoryName,
                Body = filteredLines.ToString().TrimEnd()
            };
            factory.Parameters.Add(new JavaParameter(paramType, paramName));

            javaClass.Methods.Add(factory);
        }
        // else: more than 1 param with same erasure — just silently drop for now
    }

    private static void AddMethodIfNotDuplicate(JavaClassDeclaration javaClass, JavaMethodDeclaration javaMethod)
        => AddMethodIfNotDuplicateInternal(javaClass, javaMethod);

    private static void AddCtorIfNotDuplicate(JavaClassDeclaration javaClass, JavaConstructorDeclaration ctor)
        => AddCtorIfNotDuplicateInternal(javaClass, ctor);

    /// <summary>
    /// When a C# class implements ICloneable (mapped to Java Cloneable), add a
    /// memberwiseClone() helper that wraps super.clone() so that C# MemberwiseClone()
    /// call sites compile.
    /// </summary>
    private static void AddCloneableBridgeMethods(JavaClassDeclaration javaClass)
    {
        if (!javaClass.ImplementedTypes.Any(t => t == "Cloneable")) return;
        if (javaClass.Methods.Any(m => m.Name == "memberwiseClone")) return;

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Protected,
            ReturnType = "Object",
            Name = "memberwiseClone",
            Body = "try {\n            return super.clone();\n        } catch (CloneNotSupportedException __e) {\n            throw new RuntimeException(__e);\n        }"
        });
    }

    /// <summary>
    /// When a C# class implements IEnumerator&lt;T&gt; (mapped to Java Iterator&lt;T&gt;),
    /// add hasNext() and next() bridge methods so the class satisfies the Iterator contract.
    /// </summary>
    private static void AddIteratorBridgeMethods(JavaClassDeclaration javaClass)
    {
        var iteratorType = javaClass.ImplementedTypes.FirstOrDefault(t =>
            t == "Iterator" || t.StartsWith("Iterator<"));
        if (iteratorType == null) return;
        if (javaClass.Methods.Any(m => m.Name is "hasNext" or "next")) return;

        // Extract the element type from "Iterator<T>" or fall back to "Object"
        string elementType = "Object";
        if (iteratorType.StartsWith("Iterator<") && iteratorType.EndsWith(">"))
            elementType = iteratorType.Substring(9, iteratorType.Length - 10);

        javaClass.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private,
            Type = "boolean",
            Name = "_iteratorHasNext",
            Initializer = "false"
        });

        javaClass.Methods.Insert(0, new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "boolean",
            Name = "hasNext",
            Body = "if (_iteratorHasNext) return true;\n        _iteratorHasNext = moveNext();\n        return _iteratorHasNext;"
        });

        javaClass.Methods.Insert(1, new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = elementType,
            Name = "next",
            Body = "if (!_iteratorHasNext && !moveNext()) throw new java.util.NoSuchElementException();\n        _iteratorHasNext = false;\n        return getCurrent();"
        });
    }

    /// <summary>
    /// If a class has both compareTo(SomeType) and compareTo(Object), remove the Object version.
    /// Java automatically generates a bridge compareTo(Object) → compareTo(T) for Comparable&lt;T&gt; classes,
    /// so an explicit compareTo(Object) causes a name conflict / duplicate method error.
    /// </summary>
    private static void RemoveCompareToBridgeConflicts(JavaClassDeclaration javaClass)
    {
        var compareToMethods = javaClass.Methods.Where(m => m.Name == "compareTo" && m.Parameters.Count == 1).ToList();
        if (compareToMethods.Count < 2) return;
        bool hasTyped = compareToMethods.Any(m => m.Parameters[0].Type != "Object");
        if (hasTyped)
            javaClass.Methods.RemoveAll(m => m.Name == "compareTo" && m.Parameters.Count == 1 && m.Parameters[0].Type == "Object");
    }

    /// <summary>
    /// When a C# class implements ICollection&lt;T&gt; but is mapped to Iterable&lt;T&gt; (to avoid
    /// implementing all abstract Collection methods), the generated class has getCount() but not size().
    /// Java code that calls Count on an instance will be translated to size()
    /// (the standard Java Collection method), so add a size() bridge to delegate to getCount().
    /// Similarly, add isEmpty() delegating to size() == 0.
    /// </summary>
    private static void AddIterableSizeBridgeMethods(JavaClassDeclaration javaClass)
    {
        bool hasIterable = javaClass.ImplementedTypes.Any(t => t == "Iterable" || t.StartsWith("Iterable<"));
        if (!hasIterable) return;
        if (javaClass.Methods.Any(m => m.Name == "size")) return; // already has size()

        bool hasGetCount = javaClass.Methods.Any(m => m.Name == "getCount" && m.Parameters.Count == 0);
        if (!hasGetCount) return;

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "size",
            Body = "return getCount();"
        });

        if (!javaClass.Methods.Any(m => m.Name == "isEmpty"))
        {
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Modifiers = JavaModifiers.Public,
                ReturnType = "boolean",
                Name = "isEmpty",
                Body = "return getCount() == 0;"
            });
        }
    }

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = new Transformers.Member.FieldTransformer();
                foreach (var javaField in fieldTransformer.TransformAll(fieldDecl, context))
                    javaClass.Fields.Add(javaField);
                break;

            case PropertyDeclarationSyntax propDecl:
                // Explicit interface implementations (e.g. int IEdge.Source {...}) must be generated as
                // ordinary public getter/setter methods in Java because Java interfaces don't support
                // explicit implementation hiding (all interface members are public).
                // We DO NOT skip them — we transform them as regular properties.
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection collection)
                {
                    foreach (var prop in collection.Members)
                    {
                        if (prop is JavaFieldDeclaration jf) javaClass.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) AddMethodIfNotDuplicate(javaClass, jm);
                    }
                }
                else if (props is JavaFieldDeclaration jf)
                {
                    javaClass.Fields.Add(jf);
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    AddMethodIfNotDuplicate(javaClass, jm);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                // Explicit interface implementations are generated as public methods (not skipped)
                // Exception: suppress IComparable.CompareTo(object) if class already has Comparable<T>.compareTo(T)
                // because Java automatically adds the bridge method; having an explicit one causes name conflict.
                if (methodDecl.ExplicitInterfaceSpecifier != null &&
                    methodDecl.Identifier.Text == "CompareTo" &&
                    methodDecl.ParameterList.Parameters.Count == 1 &&
                    methodDecl.ExplicitInterfaceSpecifier.Name.ToString() == "IComparable" &&
                    javaClass.Methods.Any(m => m.Name == "compareTo" && m.Parameters.Count == 1 &&
                        m.Parameters[0].Type != "Object"))
                {
                    break; // skip the IComparable.CompareTo(Object) bridge — Java adds it automatically
                }
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    AddMethodIfNotDuplicate(javaClass, javaMethod);
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = new Transformers.Member.OperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    AddMethodIfNotDuplicate(javaClass, opMethod);
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    AddCtorIfNotDuplicate(javaClass, javaCtor);
                }
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerResult = indexerTransformer.Transform(indexerDecl, context);
                if (indexerResult is JavaMemberCollection indexerCollection)
                {
                    foreach (var indexerMember in indexerCollection.Members)
                    {
                        if (indexerMember is JavaMethodDeclaration jm) AddMethodIfNotDuplicate(javaClass, jm);
                    }
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                var nestedTransformer = factory.CreateClassTransformer();
                var nestedResult = nestedTransformer.Transform(nestedClass, context);
                if (nestedResult is JavaClassDeclaration jc)
                {
                    jc.Modifiers |= JavaModifiers.Static; // C# nested types are always static in Java
                    javaClass.NestedTypes.Add(jc);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var interfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedInterfaceDecl = interfaceTransformer.Transform(nestedInterface, context);
                if (nestedInterfaceDecl is JavaInterfaceDeclaration ji)
                {
                    ji.Modifiers |= JavaModifiers.Static; // nested interfaces are also static
                    javaClass.NestedTypes.Add(ji);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                // EnumTransformer 需要 EnumDeclarationSyntax
                var enumTransformer = new Transformers.Type.EnumTransformer();
                var nestedEnumResult = enumTransformer.TransformEnum(nestedEnum, context);
                if (nestedEnumResult is JavaEnumDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static; // nested enums are static
                    javaClass.NestedTypes.Add(je);
                }
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                var eventFieldTransformer = new Transformers.Member.EventFieldTransformer();
                var eventMembers = eventFieldTransformer.TransformEvent(eventFieldDecl, context);
                foreach (var em in eventMembers)
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name)) javaClass.Fields.Add(ef);
                    if (em is JavaMethodDeclaration emm) AddMethodIfNotDuplicate(javaClass, emm);
                }
                break;

            case EventDeclarationSyntax eventDecl:
                var explicitEventTransformer = new Transformers.Member.EventFieldTransformer();
                var explicitEventMembers = explicitEventTransformer.TransformExplicitEvent(eventDecl, context);
                foreach (var em in explicitEventMembers)
                {
                    if (em is JavaFieldDeclaration ef && !javaClass.Fields.Any(f => f.Name == ef.Name)) javaClass.Fields.Add(ef);
                    if (em is JavaMethodDeclaration emm)
                    {
                        // For explicit events, REPLACE any existing fire method (from private field events) with the
                        // properly-typed version generated by the explicit event transformer.
                        var existingFireIdx = javaClass.Methods.FindIndex(m => m.Name == emm.Name);
                        if (existingFireIdx >= 0)
                            javaClass.Methods[existingFireIdx] = emm;
                        else
                            AddMethodIfNotDuplicate(javaClass, emm);
                    }
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

            case StructDeclarationSyntax nestedStruct:
                // C# nested struct → Java nested class
                var nestedStructTransformer = new Transformers.Type.StructTransformer();
                var nestedStructResult = nestedStructTransformer.Transform(nestedStruct, context);
                if (nestedStructResult is JavaClassDeclaration jcs)
                {
                    jcs.Modifiers |= JavaModifiers.Static; // nested structs/classes are static in Java
                    javaClass.NestedTypes.Add(jcs);
                }
                break;
        }
    }
}
