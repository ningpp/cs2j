using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

[TransformerRegistration]
public class IdentifierExpressionTransformer : IIRExpressionTransformer
{
    static IdentifierExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.IdentifierName,
            SyntaxKind.PredefinedType,
            SyntaxKind.GenericName,
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.PointerMemberAccessExpression
        }, new IdentifierExpressionTransformer());
    }

    private static readonly Lazy<IdentifierExpressionTransformer> _instance = new(() => new());
    public static IdentifierExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            SyntaxKind.PredefinedType => ((PredefinedTypeSyntax)node).Keyword.Text,
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            _ => throw new System.NotSupportedException($"Kind {node.Kind()}")
        };

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
        => new JavaRawExpression(Transform(node, context), "void");

    // ── IdentifierName ──────────────────────────────────────────────────────

    private static string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var symbol = context.GetSymbolInfo(node).Symbol;

        // Assembly-scoped synthetic resource types (SR → <Project>SR) are renamed at
        // declaration time; a static-member receiver or bare type reference must use
        // the same renamed identifier or the simple-name reference dangles.
        if (symbol is INamedTypeSymbol namedType
            && context.TryGetAssemblyScopedJavaTypeName(namedType, out var assemblyScopedName))
        {
            var typeNamespace = namedType.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(typeNamespace)
                && !string.Equals(typeNamespace, context.CurrentNamespace, StringComparison.Ordinal))
            {
                context.AddImport(
                    $"{context.NamespaceToPackage(typeNamespace)}.{assemblyScopedName}");
            }
            return assemblyScopedName;
        }

        // using 别名的裸类型标识（静态成员接收者、switch case 标签如
        // `case FT.FuncBoolean:`）：类型位置已按符号解析出映射名（`FT _funcType` →
        // `Function.FunctionType`），表达式位置若保留别名拼写，javac 会把 `FT.`
        // 当程序包解析（程序包FT不存在 / 找不到符号 变量 FT），这里同样落到映射名
        // （MapType 会同步登记所需导入；别名未绑定到类型时维持原样）。
        if (context.IsAlias(node.Identifier.Text)
            && context.MapAliasToJavaType(node.Identifier.Text) is { } aliasJavaType)
        {
            return aliasJavaType;
        }

        // Bare type identifiers used as static-member receivers need the same explicit
        // project-package import as type references: `dotnet.system.*` and the compat
        // wildcard otherwise both provide e.g. UriKind, which javac rejects as ambiguous.
        // 源内定义类型的接收者同理必须登记（source-defined）：其包可能是未映射
        // System.* 命名空间的恒等形态（真实链 Regex.Cache 的
        // SegmentStringBuilder.Create() → import System.Text.SegmentStringBuilder，
        // 否则 mvn 找不到符号 变量 SegmentStringBuilder）。BCL 接收者
        // （System.Math 等）不登记——残留导入会被发射期丢弃。
        if (symbol is INamedTypeSymbol receiverType
            && receiverType.ContainingType == null)
        {
            var typeNamespace = receiverType.ContainingNamespace?.ToDisplayString();
            if (typeNamespace is not null
                && !string.Equals(typeNamespace, context.CurrentNamespace, StringComparison.Ordinal))
            {
                if (typeNamespace.StartsWith("dotnet.", StringComparison.Ordinal))
                {
                    context.AddImport(
                        $"{context.NamespaceToPackage(typeNamespace)}.{context.GetJavaTopLevelTypeName(receiverType)}");
                }
                else if (receiverType.Locations.Any(static loc => loc.IsInSource))
                {
                    context.AddSourceDefinedImport(
                        $"{context.NamespaceToPackage(typeNamespace)}.{context.GetJavaTopLevelTypeName(receiverType)}");
                }
            }
        }

        // An out-var declared inside the current statement is read through its holder:
        // the value only exists after the call completes, so the read-back declaration
        // emitted after the statement is too late for same-statement uses such as
        // `d.TryGetValue(k, out v) ? v : fallback`. 同一判据也覆盖「先声明后作为
        // out 实参」的局部变量（`object obj; return d.TryGetValue(k, out obj) ? obj
        // : null;`）——其声明无初始化器，本语句内的读取同样必须先走 holder
        //（真实链 NameTable.Item 2 站 javac 可能尚未初始化变量 obj）。holder 集合
        // 按语句清空（DrainPostStatements），跨语句读取仍走变量本体。
        if (symbol is ILocalSymbol local
            && context.TryGetActiveRefHolder(
                ConversionContext.EscapeJavaKeyword(local.Name), out var outHolder)
            && (IsOutDeclaredLocal(local) || IsUninitializedLocalDeclaration(local)))
            return $"{outHolder}.value";

        // Instance fields declared by an enclosing type (or a base class) cannot be
        // referenced unqualified from a nested Java class: bind them to `this`.
        if (symbol is IFieldSymbol { IsStatic: false } field
            && field.ContainingType is { } declaringType
            && context.CurrentEnclosingRoslynType is { } currentType
            && !SymbolEqualityComparer.Default.Equals(declaringType, currentType)
            && IsOuterOrBaseType(declaringType, currentType))
        {
            var fieldName = ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
            if (IsEnclosingType(declaringType, currentType))
            {
                // A private field of the enclosing type is reachable from a nested
                // class, but only through a reference typed as that enclosing type —
                // `this.field` resolves as an inherited member and fails.
                return $"(({context.MapType(declaringType)}) this).{fieldName}";
            }
            return $"this.{fieldName}";
        }

        // Property accessed by value → getXxx()
        if (symbol is IPropertySymbol p
            && !(node.Parent is AssignmentExpressionSyntax a && a.Left == node))
            return $"get{p.Name}()";
        // Declared holder parameters (ref/out wrapped into IntHolder/ObjectHolder<...>
        // by ConvertParameter) read through `.value` at every expression position:
        // the declaration-side lowering only rewrites plain `=` assignments, so
        // compound ops, indexing and argument passing need the dereference here
        // (real chains: Decoder.Convert's `bytesUsed++`, XmlUtf8RawTextWriter's
        // paired-pointer index slots). Plain `=` keeps its dedicated branch in
        // AssignmentTransformer, which never routes the LHS through this path.
        if (symbol is IParameterSymbol { RefKind: RefKind.Ref or RefKind.Out } holderParam
            && context.MethodState.IsHolderValueParam(holderParam))
        {
            return $"{ConversionContext.EscapeJavaKeyword(node.Identifier.Text)}.value";
        }
        // Local / parameter / field / default → just the name
        return ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
    }

    /// <summary>
    /// True when <paramref name="local"/> was introduced by an <c>out</c> declaration.
    /// Such a variable is only assigned after the call runs, so reads during the same
    /// statement must go through the out holder; the read-back declaration emitted
    /// after the statement runs too late (and preprocessed trees may have been rebuilt,
    /// so identity comparisons against the declaring statement are unreliable).
    /// </summary>
    private static bool IsOutDeclaredLocal(ILocalSymbol local)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            var declared = reference.GetSyntax();
            if (declared.FirstAncestorOrSelf<DeclarationExpressionSyntax>() is { } declaration
                && declaration.Parent is ArgumentSyntax argument
                && argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>局部变量声明无初始化器（C# 里靠后续赋值/out 调用获得值）。</summary>
    private static bool IsUninitializedLocalDeclaration(ILocalSymbol local)
        => local.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: null });

    private static void AddImportForHelperReceiver(string dottedName, ConversionContext context)
    {
        var dot = dottedName.IndexOf('.');
        if (dot <= 0) return;
        var helper = dottedName[..dot];
        if (helper is "IntPtrHelper" or "MathHelper" or "StringHelper" or "ObjectHelper"
            or "ArrayHelper" or "CultureInfo" or "IPAddressHelper" or "ExceptionCompat"
            or "TypeHelper" or "TextWriterHelper" or "ReflectionHelper")
            context.AddImport($"io.github.ningpp.compat.{helper}");
    }

    private static bool IsEnclosingType(INamedTypeSymbol declaringType, INamedTypeSymbol currentType)
    {
        for (var outer = currentType.ContainingType; outer != null; outer = outer.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(outer.OriginalDefinition, declaringType.OriginalDefinition))
                return true;
        }
        return false;
    }

    private static bool IsOuterOrBaseType(INamedTypeSymbol declaringType, INamedTypeSymbol currentType)
    {
        for (var outer = currentType.ContainingType; outer != null; outer = outer.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(outer.OriginalDefinition, declaringType.OriginalDefinition))
                return true;
        }

        for (var baseType = currentType.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, declaringType.OriginalDefinition))
                return true;
        }

        return false;
    }

    // ── Member access ───────────────────────────────────────────────────────

    private static string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // Implicit method-group conversion: `SomeType.Method` passed directly as
        // a delegate argument (no explicit `new Func(...)`) → Java method ref.
        if (context.GetSymbolInfo(node).Symbol is IMethodSymbol groupMethod
            && context.GetTypeInfo(node).ConvertedType?.TypeKind == TypeKind.Delegate)
        {
            return ObjectCreationTransformer.EmitDelegateMethodReference(node, groupMethod, context);
        }
        // Static enum members on explicitly mapped enums: NormalizationForm.FormC →
        // Normalizer.Form.NFC (receiver and member both come from TypeMappings).
        if (context.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } enumField
            && enumField.ContainingType is INamedTypeSymbol enumOwner
            && enumOwner.TypeKind == TypeKind.Enum)
        {
            // 元数据 flags 枚举以 int 常量形式由 compat 提供；其类型映射为 int，
            // 但常量引用仍必须走 compat 常量宿主类名（MethodAttributes.Public）。
            // 常量宿主与 C# 简名可能不同（DateTimeStyles/StringSplitOptions 的常量
            // 宿主是 compat.IntegerHelper），优先经类型映射解析宿主名与导入；映射
            // 目标是 int 本体（BindingFlags）或无映射时回退 compat.{简名}。
            if (CSharpToJava.Core.Transformers.Type.EnumTransformer
                    .IsIntConstantMetadataEnum(enumOwner))
            {
                if (TryGetMappedStaticContainingType(enumField, context, out var hostType, out var hostImports)
                    && hostType != "int")
                {
                    foreach (var hostImport in hostImports)
                    {
                        context.AddImport(hostImport);
                    }
                    return $"{hostType}.{enumField.Name}";
                }
                context.AddImport($"io.github.ningpp.compat.{enumOwner.Name}");
                return $"{enumOwner.Name}.{enumField.Name}";
            }
            if (TryGetMappedStaticContainingType(enumField, context, out var mappedEnumType, out var enumImports))
            {
                foreach (var import in enumImports)
                    context.AddImport(import);
                var mappedMember = context.TypeMappings.MapMethod(
                    enumField.ContainingType.ToDisplayString(), enumField.Name) ?? enumField.Name;
                return $"{mappedEnumType}.{mappedMember}";
            }
        }

        // Mapped static constants on compat types: TimeSpan.Zero is a static
        // readonly field → CSharpTimeSpan.ZERO (the mapped Java type keeps its
        // constants under Java-style names).
        if (context.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } mappedStaticField
            && TryGetMappedStaticContainingType(
                mappedStaticField, context, out var constantOwner, out var constantImports)
            && context.TypeMappings.MapMethod(
                mappedStaticField.ContainingType.ToDisplayString(), mappedStaticField.Name)
                is { } mappedConstant
            && !mappedConstant.Contains('('))
        {
            foreach (var import in constantImports)
                context.AddImport(import);
            // Method-shaped mapped values must render as invocations:
            // IPAddress.Loopback → InetAddress.getLoopbackAddress() and
            // IPAddress.IPv6Loopback → IPAddressHelper.ipv6Loopback(); a bare
            // field-style access leaves javac looking for a nonexistent variable.
            if (mappedConstant.Contains('.'))
            {
                AddImportForHelperReceiver(mappedConstant, context);
                return $"{mappedConstant}()";
            }
            if (mappedConstant.Length > 3
                && mappedConstant.StartsWith("get", StringComparison.Ordinal)
                && char.IsUpper(mappedConstant[3]))
            {
                return $"{constantOwner}.{mappedConstant}()";
            }
            return $"{constantOwner}.{mappedConstant}";
        }

        // System.String.Empty is a static readonly field in C#; Java has no such
        // field on java.lang.String, and the empty literal is the exact equivalent.
        if (context.GetSymbolInfo(node).Symbol
            is IFieldSymbol { IsStatic: true, Name: "Empty" } emptyField
            && emptyField.ContainingType?.SpecialType == SpecialType.System_String)
        {
            return "\"\"";
        }

        // System.Guid.Empty：Guid 映射 compat UUID（宿主 java.util.UUID 无 Empty 字段），
        // 常量由 compat UUID 桥承载（UUID.Empty）。缺此分支时字段名原样透传为
        // `Guid.Empty`（javac 找不到符号 变量 Guid；真实链 XmlConvert.TryToGuid）。
        if (context.GetSymbolInfo(node).Symbol
            is IFieldSymbol { IsStatic: true, Name: "Empty" } guidEmptyField
            && guidEmptyField.ContainingType?.ToDisplayString() == "System.Guid")
        {
            context.AddImport("io.github.ningpp.compat.UUID");
            return "UUID.Empty";
        }

        // System.Type.EmptyTypes has no counterpart on java.lang.Class (the mapped
        // type of System.Type): the exact equivalent is a zero-length Class[]. It
        // appears in GetConstructor argument positions
        // (TypeHelper.getConstructor(type, Type.EmptyTypes)), so a bare
        // `Type.EmptyTypes` leaves javac looking for a nonexistent `Type` variable.
        if (context.GetSymbolInfo(node).Symbol
            is IFieldSymbol { IsStatic: true, Name: "EmptyTypes" } emptyTypesField
            && emptyTypesField.ContainingType?.ToDisplayString() == "System.Type")
        {
            return "new Class[0]";
        }

        // Primitive static fields on keyword qualifiers: int.MaxValue (the
        // predefined keyword, not the System.Int32 qualified name) must become
        // Integer.MAX_VALUE — Java primitives cannot be dereferenced.
        if (context.GetSymbolInfo(node).Symbol is IFieldSymbol { IsStatic: true } primitiveField)
        {
            // compat Decimal keeps its constants under Java-style names.
            if (primitiveField.ContainingType?.SpecialType == SpecialType.System_Decimal)
            {
                var decimalConstant = primitiveField.Name switch
                {
                    "MaxValue" => "Decimal.MAX_VALUE",
                    "MinValue" => "Decimal.MIN_VALUE",
                    "Zero" => "Decimal.ZERO",
                    "One" => "Decimal.ONE",
                    "MinusOne" => "Decimal.MINUS_ONE",
                    _ => null,
                };
                if (decimalConstant != null)
                {
                    context.AddImport("io.github.ningpp.compat.Decimal");
                    return decimalConstant;
                }
            }
            if (TryRenderPrimitiveStaticField(
                    primitiveField, context.GetTypeInfo(node).ConvertedType, out var primitiveFieldText))
            {
                return primitiveFieldText;
            }
        }

        // System.Console.Error/Out are stream properties, not bean accessors:
        // they map directly to java.lang.System's static err/out fields.
        if (context.GetSymbolInfo(node).Symbol
            is IPropertySymbol { ContainingType: { } consoleType } consoleStream
            && consoleType.ToDisplayString() == "System.Console")
        {
            if (consoleStream.Name == "Error") return "System.err";
            if (consoleStream.Name == "Out") return "System.out";
        }

        // System.Net.IPAddress.Loopback / IPv6Loopback: the static members live on
        // different compat surfaces (InetAddress / IPAddressHelper), so resolve them
        // syntactically even when the member symbol is unavailable post-preprocessing.
        if (node.Name.Identifier.Text is "Loopback" or "IPv6Loopback"
            && context.GetTypeInfo(node.Expression).Type?.ToDisplayString() == "System.Net.IPAddress")
        {
            if (node.Name.Identifier.Text == "Loopback")
            {
                context.AddImport("java.net.InetAddress");
                return "InetAddress.getLoopbackAddress()";
            }
            context.AddImport("io.github.ningpp.compat.IPAddressHelper");
            return "IPAddressHelper.ipv6Loopback()";
        }

        // Synthesized pass output (e.g. iterator state machines) can reach the
        // transformer without semantic binding; resolve well-known static members
        // by syntax so compat constants/accessors still render.
        if (context.GetSymbolInfo(node).Symbol is null
            && TryRenderUnboundStaticMember(
                node.Expression, node.Name.Identifier.Text, context, out var unboundMember))
        {
            return unboundMember;
        }

        var receiver = ExpressionTransformerFacade.Instance.Transform(node.Expression, context);
        var symbol = context.GetSymbolInfo(node).Symbol;
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);

        // C# 元组元素 tuple.ItemN（含 System.Tuple<T1,...> 的 ItemN）→ vavr 访问器
        // _N()：元组类型映射为 io.vavr.TupleN，成员名原样发射会让 javac 找不到
        // 符号 变量 ItemN（真实链 XmlTextReaderImpl 的 tuple_1.Item2 等 4 处）。
        if (IsTupleItemName(node.Name.Identifier.Text, out var tupleItemIndex))
        {
            INamedTypeSymbol? tupleType = null;
            if (symbol is IFieldSymbol tupleItemField
                && tupleItemField.ContainingType is INamedTypeSymbol tupleItemCt
                && (tupleItemCt.IsTupleType
                    || tupleItemCt.ToDisplayString().StartsWith("System.Tuple", StringComparison.Ordinal)))
            {
                tupleType = tupleItemCt;
            }
            else
            {
                var tupleReceiverType = context.GetTypeInfo(node.Expression).Type
                                        ?? context.GetTypeInfo(node.Expression).ConvertedType;
                if (tupleReceiverType is INamedTypeSymbol tupleRt
                    && (tupleRt.IsTupleType
                        || tupleRt.ToDisplayString().StartsWith("System.Tuple", StringComparison.Ordinal)))
                {
                    tupleType = tupleRt;
                }
            }
            if (tupleType != null)
            {
                // System.Tuple`2 的映射是 java.util.Map.Entry（声明侧变量/返回类型发
                // Map.Entry，实例化侧发 AbstractMap.SimpleImmutableEntry）：调用侧
                // 元素访问必须同键，落 getKey()/getValue()；发 vavr 的 _N() 会让
                // javac 在 Map.Entry 上找不到 _1/_2（真实链 DtdParser/
                // XsdValidatingReader 异步 out 参数链）。其余元组形态（tuple 语法
                // ValueTuple/System.Tuple`3+）映射 vavr TupleN，保持 _N()。
                if (MapsToMapEntry(tupleType, context))
                {
                    return tupleItemIndex == 1 ? $"{receiver}.getKey()" : $"{receiver}.getValue()";
                }
                return $"{receiver}._{tupleItemIndex}()";
            }
        }

        // Static members qualified by a type with an explicit type mapping: when
        // the mapped Java type keeps the same simple name as the C# type, rewrite
        // the qualifier to that simple name and register its import
        // (System.StringComparison.OrdinalIgnoreCase → StringComparison.OrdinalIgnoreCase
        // + import). Mapped wrapper types with a different Java simple name keep
        // their original receiver, because their members own dedicated renderings
        // (IPAddress.Loopback → getLoopbackAddress, Double.NaN → the boxed
        // wrapper constant) and the invocation/property passes handle them.
        if (symbol is { IsStatic: true }
            && TryGetMappedStaticContainingType(symbol, context,
                out var mappedStaticType, out var mappedImports)
            && mappedStaticType == symbol.ContainingType.Name)
        {
            receiver = mappedStaticType;
            foreach (var import in mappedImports)
            {
                context.AddImport(import);
            }
        }

        // Property (read) → getXxx()
        if (symbol is IPropertySymbol property
            && !(node.Parent is AssignmentExpressionSyntax a && a.Left == node))
        {
            // C# IntPtr/nint is reported with the alias display "nint".
            if (property.Name == "Size"
                && property.ContainingType?.SpecialType == SpecialType.System_IntPtr)
            {
                context.AddImport("io.github.ningpp.compat.IntPtrHelper");
                return "IntPtrHelper.getSize()";
            }

            // System.Type.GenericTypeArguments has no java.lang.Class counterpart;
            // the compat helper takes the receiver as its argument.
            if (property.Name == "GenericTypeArguments"
                && property.ContainingType?.ToDisplayString() == "System.Type")
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.getGenericTypeArguments({receiver})";
            }
            // C22: System.Nullable<T> surfaces — HasValue is a reference null
            // check and Value is a guarded unbox on the Java wrapper (the C22
            // preprocessor only leaves .Value inside a HasValue guard).
            if (TryTransformNullableMember(property, receiver, context, out var nullableMember))
            {
                return nullableMember;
            }

            // Arrays expose a public final length field in Java.
            if (property.Name == "Length"
                && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol)
            {
                return $"{receiver}.length";
            }

            // C# string.Length -> java.lang.String.length(). Keyed on the semantic
            // special type: ToDisplayString() spells System.String as the keyword
            // "string", which misses the "System.String" rows in TypeMappings.json.
            if (property.Name == "Length"
                && property.ContainingType?.SpecialType == SpecialType.System_String)
            {
                return $"{receiver}.length()";
            }

            // Honor TypeMappings property→method renames (e.g.
            // ThreadLocal<T>.IsValueCreated → isValueCreated()); the C09
            // ThreadLocal compat surface uses bean-style accessors that do not
            // follow the default getXxx derivation).
            var mappedProperty = context.TypeMappings.MapMethod(
                property.ContainingType.ToDisplayString(), property.Name);
            if (mappedProperty?.Contains('.') == true)
            {
                // Fully-mapped helper call: IntPtr.Size → IntPtrHelper.getSize().
                // Some rows already spell the invocation with parentheses
                // (CultureInfo.InvariantCulture → "CultureInfo.getInvariantCulture()").
                AddImportForHelperReceiver(mappedProperty, context);
                if (mappedProperty.EndsWith(')'))
                    return mappedProperty;
                // INSTANCE properties mapped to a static helper (X.Assembly →
                // TypeHelper.getAssembly) take the receiver as the first
                // argument; static properties (IntPtr.Size) carry no receiver.
                if (!property.IsStatic)
                    return $"{mappedProperty}({receiver})";
                return $"{mappedProperty}()";
            }
            // Non-bean mapped member names (Thread.CurrentThread → currentThread)
            // keep the mapped owner/receiver and become a call.
            if (!string.IsNullOrEmpty(mappedProperty)
                && !JavaNaming.IsBeanAccessorStyleName(mappedProperty)
                && mappedProperty is not ("length" or "size")
                && !mappedProperty.Contains('('))
            {
                if (property.IsStatic
                    && TryGetMappedStaticContainingType(property, context, out var staticOwner, out var staticImports)
                    && staticOwner != property.ContainingType.Name)
                {
                    foreach (var import in staticImports)
                        context.AddImport(import);
                    return $"{staticOwner}.{mappedProperty}()";
                }
                return $"{receiver}.{mappedProperty}()";
            }

            if (JavaNaming.IsBeanAccessorStyleName(mappedProperty))
            {
                if (property.IsStatic
                    && TryGetMappedStaticContainingType(property, context, out var mappedOwner, out var propertyImports))
                {
                    foreach (var import in propertyImports)
                        context.AddImport(import);
                    // System.Net.IPAddress maps to java.net.InetAddress: the mapped
                    // static property lives on the mapped Java type, not the C# name.
                    if (mappedOwner != property.ContainingType.Name)
                        return $"{mappedOwner}.{mappedProperty}()";
                }
                return $"{receiver}.{mappedProperty}()";
            }

            // Field-style mapped names: arrays were handled above; String/StringBuilder
            // Length → length() as a method-style accessor (project-pipeline companion
            // of the single-file LowerProperty pass). Collection size() likewise.
            // The receiver-type gate is required because other mapped types reuse the
            // "length" spelling (System.Array → CSharpArray.Length maps to getLength()).
            var lengthHost = property.ContainingType.ToDisplayString();
            if (mappedProperty == "length"
                && (lengthHost == "System.String"
                    || lengthHost == "System.Text.StringBuilder"))
            {
                return $"{receiver}.length()";
            }

            if (mappedProperty == "size")
            {
                return $"{receiver}.size()";
            }

            // C11 访问器形态：setter 可访问性是 private protected（Java 无法表达）时，
            // member-desugar 在声明项目把属性展开成 get_X/set_X 方法，Java 侧渲染为
            // X()/X(v)。plan 按项目计算，跨项目使用点不经使用点改写，只能按访问器
            // 形态发 X()——否则 getXxx() 找不到符号（真实链 Capture.Index/Length 被
            // dotnet-xml 的 XmlConvert 读取 6 站）。
            if (property.SetMethod is { DeclaredAccessibility: Accessibility.ProtectedAndInternal })
            {
                return $"{receiver}.{member}()";
            }

            return $"{receiver}.get{member.Substring(0, 1).ToUpperInvariant()}{member.Substring(1)}()";
        }

        // Array .Length that the semantic model does not surface as a property.
        if (member == "Length"
            && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol)
        {
            return $"{receiver}.length";
        }

        return $"{receiver}.{member}";
    }

    /// <summary>
    /// Syntax-level fallback for well-known static members whose semantic symbol is
    /// missing (synthesized pass output such as iterator state machines): maps the
    /// C# spelling to the compat/Java surface without a binding model.
    /// </summary>
    private static bool TryRenderUnboundStaticMember(
        ExpressionSyntax receiverSyntax, string memberName,
        ConversionContext context, out string rendered)
    {
        rendered = string.Empty;
        var receiverName = receiverSyntax switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            PredefinedTypeSyntax predefined => predefined.Keyword.Text,
            _ => null,
        };
        if (receiverName is null)
            return false;

        switch (receiverName, memberName)
        {
            case ("TimeSpan", "Zero"):
                context.AddImport("io.github.ningpp.compat.CSharpTimeSpan");
                rendered = "CSharpTimeSpan.ZERO";
                return true;
            case ("TimeSpan", "MaxValue"):
                context.AddImport("io.github.ningpp.compat.CSharpTimeSpan");
                rendered = "CSharpTimeSpan.MAX_VALUE";
                return true;
            case ("TimeSpan", "MinValue"):
                context.AddImport("io.github.ningpp.compat.CSharpTimeSpan");
                rendered = "CSharpTimeSpan.MIN_VALUE";
                return true;
            case ("Thread", "CurrentThread"):
                context.AddImport("io.github.ningpp.compat.ThreadHelper");
                rendered = "ThreadHelper.currentThread()";
                return true;
            case ("int", "MaxValue"): rendered = "Integer.MAX_VALUE"; return true;
            case ("int", "MinValue"): rendered = "Integer.MIN_VALUE"; return true;
            case ("long", "MaxValue"): rendered = "Long.MAX_VALUE"; return true;
            case ("long", "MinValue"): rendered = "Long.MIN_VALUE"; return true;
            case ("short", "MaxValue"): rendered = "Short.MAX_VALUE"; return true;
            case ("short", "MinValue"): rendered = "Short.MIN_VALUE"; return true;
            case ("byte", "MaxValue"): rendered = "Byte.MAX_VALUE"; return true;
            case ("byte", "MinValue"): rendered = "Byte.MIN_VALUE"; return true;
            case ("char", "MaxValue"): rendered = "Character.MAX_VALUE"; return true;
            case ("char", "MinValue"): rendered = "Character.MIN_VALUE"; return true;
            case var (keyword, _) when keyword is "double" or "float"
                && TryRenderRealWrapperConstant(keyword == "double" ? "Double" : "Float", memberName, out rendered):
                return true;
            default: return false;
        }
    }

    /// <summary>
    /// Resolves the mapped Java simple type name for a static member's containing type
    /// when an explicit type mapping exists (<c>System.StringComparison</c> →
    /// <c>StringComparison</c> + compat import). Returns false for project types and
    /// types that merely fall back to their simple name. This lookup is pure: callers
    /// decide whether to register the imports.
    /// </summary>
    private static bool TryGetMappedStaticContainingType(
        ISymbol symbol,
        ConversionContext context,
        out string mappedType,
        out IReadOnlyList<string> mappedImports)
    {
        mappedType = string.Empty;
        mappedImports = Array.Empty<string>();

        if (symbol.ContainingType is not INamedTypeSymbol type)
        {
            return false;
        }

        var display = type.ToDisplayString();
        var fqn = $"{type.ContainingNamespace}.{type.Name}";
        var configKey = context.TypeMappings.HasTypeMapping(display) ? display
            : context.TypeMappings.HasTypeMapping(fqn) ? fqn
            : null;
        if (configKey == null)
        {
            return false;
        }

        mappedType = context.TypeMappings.MapType(configKey);
        mappedImports = context.TypeMappings.GetRequiredImports(configKey);
        return true;
    }

    /// <summary>
    /// Renders integral primitive constant fields (<c>int.MaxValue</c> →
    /// <c>Integer.MAX_VALUE</c>). Signed types map to the Java wrapper
    /// constants. C# byte/uint are stored in Java signed primitives with the
    /// same bit width, so their extremes render as narrowing-cast literals
    /// (<c>byte.MaxValue</c> → <c>(byte)255</c>, <c>uint.MaxValue</c> →
    /// <c>(int)4294967295L</c>). float/double surface NaN/Infinity/extremes on
    /// the Java wrapper classes too — Java primitive keywords cannot be
    /// dereferenced, so <c>double.NaN</c> is illegal as-is; MinValue semantics
    /// differ (C# most-negative vs Java MIN_VALUE smallest-positive), so it
    /// renders as the negated MAX_VALUE.
    /// </summary>
    private static bool TryRenderPrimitiveStaticField(
        IFieldSymbol field, ITypeSymbol? convertedType, out string rendered)
    {
        rendered = "";
        switch (field.ContainingType?.SpecialType)
        {
            case SpecialType.System_Byte when field.Name is "MaxValue" or "MinValue":
                rendered = field.Name == "MaxValue" ? "(byte)255" : "(byte)0";
                return true;
            case SpecialType.System_UInt32 when field.Name is "MaxValue" or "MinValue":
                // uint.MaxValue is 4294967295. When the C# context widens it to a
                // wider numeric type (e.g. `long x = uint.MaxValue`), the Java value
                // must keep the magnitude; only an int context uses the -1 bit pattern.
                var wide = convertedType?.SpecialType
                    is SpecialType.System_Int64 or SpecialType.System_UInt64
                    or SpecialType.System_Decimal or SpecialType.System_Double
                    or SpecialType.System_Single;
                rendered = field.Name == "MaxValue"
                    ? (wide ? "4294967295L" : "(int)4294967295L")
                    : "0";
                return true;
            // ushort maps to Java int, so both extremes are plain int literals.
            case SpecialType.System_UInt16 when field.Name is "MaxValue" or "MinValue":
                rendered = field.Name == "MaxValue" ? "65535" : "0";
                return true;
            // ulong 极值落在 long 载体位型：MaxValue=0xFFFF...F → -1L（无符号
            // 消费面 UnsignedMath.toDecimalUInt64/toDoubleUInt64 均按位型解释，
            // toDecimalUInt64(-1) 即 2^64-1）；MinValue → 0L。
            case SpecialType.System_UInt64 when field.Name is "MaxValue" or "MinValue":
                rendered = field.Name == "MaxValue" ? "-1L" : "0L";
                return true;
            case SpecialType.System_Double or SpecialType.System_Single:
                var realWrapper = field.ContainingType?.SpecialType == SpecialType.System_Double ? "Double" : "Float";
                if (TryRenderRealWrapperConstant(realWrapper, field.Name, out rendered)) return true;
                break;
        }

        var wrapper = field.ContainingType?.SpecialType switch
        {
            SpecialType.System_SByte => "Byte",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_Int32 => "Integer",
            SpecialType.System_Int64 => "Long",
            SpecialType.System_Char => "Character",
            _ => null,
        };
        if (wrapper == null) return false;
        var constant = field.Name switch
        {
            "MaxValue" => "MAX_VALUE",
            "MinValue" => "MIN_VALUE",
            _ => null,
        };
        if (constant == null) return false;
        rendered = $"{wrapper}.{constant}";
        return true;
    }

    /// <summary>C# System.Double/System.Single named constants on the Java
    /// Double/Float wrappers. Epsilon is the smallest positive value, which is
    /// exactly Java's MIN_VALUE; MinValue is the most negative finite value,
    /// which Java spells as -MAX_VALUE (parenthesized so it composes inside
    /// larger expressions and initializers).</summary>
    private static bool TryRenderRealWrapperConstant(string wrapper, string fieldName, out string rendered)
    {
        rendered = fieldName switch
        {
            "NaN" => $"{wrapper}.NaN",
            "PositiveInfinity" => $"{wrapper}.POSITIVE_INFINITY",
            "NegativeInfinity" => $"{wrapper}.NEGATIVE_INFINITY",
            "MaxValue" => $"{wrapper}.MAX_VALUE",
            "MinValue" => $"(-{wrapper}.MAX_VALUE)",
            "Epsilon" => $"{wrapper}.MIN_VALUE",
            _ => "",
        };
        return rendered != "";
    }

    // ── System.Nullable<T> member surface (C22) ─────────────────────────────

    /// <summary>
    /// Maps <c>Nullable&lt;T&gt;.HasValue</c> to a reference null check and
    /// <c>.Value</c> to the guarded primitive unbox accessor. Enum/struct
    /// underlying types are reference types in Java, so <c>.Value</c> is the
    /// receiver itself.
    /// </summary>
    private static bool TryTransformNullableMember(
        IPropertySymbol property, string receiver, ConversionContext context, out string mapped)
    {
        mapped = "";
        var owner = property.ContainingType;
        if (owner is null || owner.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            return false;
        }

        switch (property.Name)
        {
            case "HasValue":
                mapped = $"({receiver} != null)";
                return true;
            case "Value":
                mapped = UnboxNullableValue(owner.TypeArguments[0], receiver, context);
                return true;
            default:
                return false;
        }
    }

    private static string UnboxNullableValue(ITypeSymbol underlying, string receiver, ConversionContext context)
    {
        // C# Nullable<T>.Value throws InvalidOperationException (mapped to
        // IllegalStateException) on null; plain Java unboxing would NPE. Route
        // primitive unwraps through the compat Nullable guard.
        if (underlying.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Char
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double)
        {
            context.AddImport("io.github.ningpp.compat.Nullable");
            return $"Nullable.value({receiver})";
        }
        // Enums and other value types are Java reference types already.
        return receiver;
    }

    // ── Generic name ────────────────────────────────────────────────────────

    private static string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var name = ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
        var args = new List<string>();
        foreach (var a in node.TypeArgumentList.Arguments)
        {
            var t = context.GetTypeInfo(a).Type;
            args.Add(t != null ? context.MapType(t) : "Object");
        }
        return $"{name}<{string.Join(", ", args)}>";
    }

    // ── Tuple helpers (consumed by other transformers) ──────────────────────

    internal static bool IsTupleItemName(string memberName, out int index)
    {
        index = 0;
        if (!memberName.StartsWith("Item") || memberName.Length < 5 || !int.TryParse(memberName[4..], out var n))
            return false;
        index = n;
        return true;
    }

    /// <summary>
    /// True when the tuple shape's configured mapping is java.util.Map.Entry
    /// (System.Tuple`2 → Map.Entry). Tuple-syntax ValueTuple and System.Tuple`3+
    /// map to vavr TupleN and keep the _N() accessors.
    /// </summary>
    private static bool MapsToMapEntry(INamedTypeSymbol tupleType, ConversionContext context)
    {
        if (tupleType.IsTupleType) return false;
        var def = tupleType.OriginalDefinition;
        if (def.ContainingNamespace?.ToDisplayString() != "System"
            || def.Name != "Tuple"
            || tupleType.Arity != 2)
            return false;
        return context.TypeMappings.HasTypeMapping("System.Tuple`2")
            && string.Equals(
                context.TypeMappings.MapType("System.Tuple`2"),
                "Map.Entry",
                StringComparison.Ordinal);
    }

    internal static int TryGetTupleElementIndex(INamedTypeSymbol tupleType, string elementName)
    {
        if (IsTupleItemName(elementName, out var idx)) return idx - 1;
        var fields = tupleType.TupleElements;
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Name == elementName) return i;
        return -1;
    }
}
