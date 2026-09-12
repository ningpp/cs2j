using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 属性转换器 - 将 C# 属性转换为 Java 字段和 getter/setter 方法
/// </summary>
public class PropertyTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not PropertyDeclarationSyntax propDecl)
        {
            throw new ArgumentException($"Expected PropertyDeclarationSyntax, got {node.GetType()}");
        }

        // Properties cannot be async in C#; reset any leaked async context from previous members
        context.IsInAsyncContext = false;

        var results = new List<JavaSyntaxNode>();
        var typeInfo = context.GetTypeInfo(propDecl.Type);
        var propType = typeInfo.Type != null
            ? context.MapType(typeInfo.Type)
            : context.MapTypeFromSyntax(propDecl.Type);
        var propertySymbol = context.GetDeclaredSymbol(propDecl) as IPropertySymbol;
        // Prefer the semantic symbol name: source text may contain an escape
        // sequence (e.g. `\u0467`) that must NOT leak into the Java identifier —
        // getter/setter call sites are built from the symbol's real characters.
        // Explicit interface implementations report a qualified symbol name
        // (e.g. IEntity.Name), so their Java accessor must use the syntax name.
        var propName = ConversionContext.EscapeJavaKeyword(
            propertySymbol is { ExplicitInterfaceImplementations.Length: > 0 }
                ? propDecl.Identifier.Text
                : (propertySymbol?.Name ?? propDecl.Identifier.Text));
        // Java targets disambiguate colliding auto-property backing names (e.g. explicit
        // field `myString` + property `MyString`) with a Value suffix; the TypeScript
        // property-reconstruction pass relies on the plain camelCase backing name.
        var fieldName = propertySymbol != null && context.Options.TargetLanguage != TargetLanguage.TypeScript
            ? CSharpToJava.Core.Context.JavaNaming.GetAutoPropertyBackingFieldName(propertySymbol)
            : ConversionContext.EscapeJavaKeyword(ToCamelCase(propName));
        var isStatic = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var modifiers = ConvertModifiers(propDecl.Modifiers, isStatic);
        var previousStaticContext = context.IsInStaticMember;
        if (isStatic)
            context.IsInStaticMember = true;
        var isExplicitInterfaceImplementation = propertySymbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 };
        var propertyComments = context.GetDeclarationComments(propDecl, propertySymbol).ToCombinedComment();

        var isEncodingPreambleProperty =
            propertySymbol is IPropertySymbol
            {
                Name: "Preamble",
                ContainingType: { } containingType
            }
            && IsSystemTextEncodingType(containingType);

        // 判断是否有显式实现
        var hasGetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        var getAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        // 处理自动属性 vs 显式属性
        // Expression-bodied properties (e.g. public int X => expr;) are NOT auto-properties —
        // they have no backing field and should only produce a getter method.
        var isExpressionBodiedProperty = propDecl.ExpressionBody != null;
        var isAutoProperty = !isExpressionBodiedProperty
                          && getAccessor?.Body == null
                          && getAccessor?.ExpressionBody == null
                          && (setAccessor == null
                              || (setAccessor.Body == null && setAccessor.ExpressionBody == null));

        // Detect if this property hides a base class member (C# `new` keyword or implicit hiding).
        // In C#, hiding is non-virtual: base-typed references call the base getter.
        // In Java, all methods are virtual, so generating an override would cause incorrect
        // dispatch. Hiding properties with the same return type are skipped.
        // Properties that return a more derived type are still emitted, because callers
        // inside the derived class rely on the derived return type.
        var isHidingBaseMember = IsHidingBaseMember(propertySymbol);

        // A hiding property whose getter/setter purely delegate to the base property
        // (e.g. `public new T X { get => base.X; set => base.X = value; }`) exists only
        // to widen accessibility (e.g. protected -> public). It must still be emitted in
        // Java as super.getX()/super.setX(value) so callers resolve to the public bridge
        // rather than the inaccessible base member. This is semantically safe because the
        // body explicitly calls super — there is no non-virtual dispatch concern (the
        // existing expression transformer already turns `base.X` into `super.getX()` and
        // `base.X = value` into `super.setX(value)`).
        var isPureBaseDelegation = isHidingBaseMember && IsPureBaseDelegationProperty(propDecl);

        // 检查是否是只读属性（只有 getter）
        var isReadOnly = hasGetter && !hasSetter;
        // 检查是否是只写属性（只有 setter）
        var isWriteOnly = hasSetter && !hasGetter;

        // Java 字段修饰符 - 字段应该是 private，除非是 static
        var fieldModifiers = JavaModifiers.Private;
        if (isStatic)
        {
            fieldModifiers |= JavaModifiers.Static;
        }

        // 创建后备字段 - 只有自动属性才需要后备字段
        // 显式属性（有body的getter/setter）直接在getter/setter中操作现有字段，不需要生成后备字段
        var isAbstract = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        var needsBackingField = (isAutoProperty || propDecl.Initializer != null)
                             && !isAbstract
                             && !context.IsInInterfaceBody;

        var field = new JavaFieldDeclaration
        {
            Name = fieldName,
            Type = propType,
            Modifiers = fieldModifiers
        };

        // C# structs are value types that can never be null — initialize backing fields
        // with default instances so Java code doesn't encounter null struct references.
        if (propDecl.Initializer == null && needsBackingField
            && typeInfo.Type?.SpecialType == SpecialType.System_Decimal)
        {
            context.AddImport("io.github.ningpp.compat.Decimal");
            field.Initializer = "Decimal.ZERO";
        }
        else if (propDecl.Initializer == null && needsBackingField
            && StructCloneHelper.IsUserDefinedStruct(typeInfo.Type))
        {
            // Round 20（TS2673）：与 FieldTransformer 结构体字段默认零值填充同规——
            // TS 目标下工厂化结构体（含隐式无参构造器集合 >1）直发 `new X()` 报
            // TS2673，零参隐式构造器恒为 Create$1；未工厂化/非本编译单元保持 new。
            if (context.Options.TargetLanguage == TargetLanguage.TypeScript
                && typeInfo.Type is INamedTypeSymbol structSymbol
                && structSymbol.InstanceConstructors.Length > 1
                && structSymbol.Locations.Any(location => location.IsInSource))
            {
                var barePropType = propType.Contains('<') ? propType[..propType.IndexOf('<')] : propType;
                field.Initializer = $"{barePropType}.Create$1()";
            }
            else
            {
                field.Initializer = $"new {propType}()";
            }
        }
        else if (propDecl.Initializer == null && needsBackingField
            && propertySymbol?.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            // C# enum auto-properties default to the member with value 0, but Java enum
            // references default to null — initialize the backing field to the 0-valued member.
            bool isFlags = context.IsFlagsEnum(enumType.Name)
                || enumType.GetAttributes().Any(a =>
                    a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (!isFlags)
            {
                var enumTypeRef = BuildEnumTypeReference(enumType);
                // A field/property with the same name as the enum type makes the simple
                // reference self-referencing in Java — qualify with the package.
                if (context.CurrentEnclosingRoslynType != null
                    && context.CurrentEnclosingRoslynType.GetMembers(enumTypeRef)
                        .Any(m => m is IFieldSymbol or IPropertySymbol))
                {
                    var enumNs = enumType.ContainingNamespace?.ToDisplayString();
                    if (!string.IsNullOrEmpty(enumNs) && enumNs != "<global namespace>")
                    {
                        var enumJavaPkg = context.NamespaceToPackage(enumNs);
                        if (!string.IsNullOrEmpty(enumJavaPkg))
                            enumTypeRef = $"{enumJavaPkg}.{enumTypeRef}";
                    }
                }
                var zeroMember = FindEnumMemberByValue(enumType, 0);
                field.Initializer = zeroMember != null
                    ? $"{enumTypeRef}.{zeroMember}"
                    : $"{enumTypeRef}.values()[0]";
            }
        }

        // 如果有默认值
        if (propDecl.Initializer != null)
        {
            field.Initializer = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(propDecl.Initializer.Value, context);
            field.Initializer = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                propDecl.Initializer.Value,
                field.Initializer,
                typeInfo.Type,
                context);

            // IEnumerable / IEnumerable<T> properties initialized with an array:
            // Java arrays are not compat iterables, so wrap them.
            if (propertySymbol?.Type is INamedTypeSymbol propIterable
                && (propIterable.OriginalDefinition?.ToDisplayString()
                        == "System.Collections.Generic.IEnumerable<T>"
                    || propIterable.ToDisplayString() == "System.Collections.IEnumerable")
                && context.SemanticModel != null
                && context.GetTypeInfo(propDecl.Initializer.Value).Type is IArrayTypeSymbol)
            {
                field.Initializer = ExpressionTransformerHelpers.WrapArrayForIterableTarget(
                    propIterable, field.Initializer!, context);
            }

            // Object/collection initializers emit multi-statement pre-statements
            // (var _objN = new T(); _objN.setX(...);) that cannot live inside a Java
            // field initializer. Lift them into constructors / the static block.
            if (context.HasPendingPreStatements)
            {
                var statements = new List<string>();
                statements.AddRange(context.DrainBranchScopedPreStatements());
                statements.AddRange(context.DrainPreStatements());
                if (statements.Count > 0)
                {
                    var fieldRef = isStatic
                        ? fieldName
                        : $"this.{fieldName}";
                    statements.Add($"{fieldRef} = {field.Initializer};");
                    field.InitializerStatements = statements;
                    field.Initializer = null;
                }
            }
        }

        if (needsBackingField)
        {
            ApplyXmlSerializationAnnotations(propDecl, field, context);
            results.Add(field);
        }

        // 创建 getter
        if (ShouldEmitHidingAccessors(isHidingBaseMember, isPureBaseDelegation, propDecl, needsBackingField)
            && (hasGetter || propDecl.AccessorList == null))  // 默认有 getter
        {
            var getterModifiers = modifiers;
            // 如果属性本身没有访问修饰符，默认为 public
            if (!propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) ||
                                          m.IsKind(SyntaxKind.ProtectedKeyword) ||
                                          m.IsKind(SyntaxKind.PrivateKeyword) ||
                                          m.IsKind(SyntaxKind.InternalKeyword)))
            {
                getterModifiers = JavaModifiers.Public | (modifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // If the accessor has an explicit access modifier, use that instead of the property-level one.
            // Only visibility modifiers count — `readonly get` (C# 8 readonly struct members) must not
            // strip the visibility inherited from the property.
            if (getAccessor?.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword)
                    || m.IsKind(SyntaxKind.ProtectedKeyword)
                    || m.IsKind(SyntaxKind.InternalKeyword)) == true)
            {
                var accessorVisibility = ConvertModifiers(getAccessor.Modifiers, isStatic: false);
                getterModifiers = (getterModifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private))
                                | (accessorVisibility & (JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // 确保只有一个访问修饰符
            getterModifiers = GetSingleAccessModifier(getterModifiers);

            var getter = new JavaMethodDeclaration
            {
                Name = isEncodingPreambleProperty
                    ? "getPreambleSpan"
                    : JavaNaming.GetPropertyGetterName(propName) + GetPropertyHidingSuffix(propertySymbol),
                ReturnType = GetOverrideAwarePropertyType(propDecl, propType, context),
                Modifiers = getterModifiers,
                LeadingComment = propertyComments,
                Body = isExpressionBodiedProperty
                    ? Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(propDecl.ExpressionBody!.Expression, context)
                    : getAccessor?.ExpressionBody != null
                        ? Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(getAccessor.ExpressionBody.Expression, context)
                        : (StructCloneHelper.IsUserDefinedStruct(typeInfo.Type)
                            ? $"return {fieldName}.clone();"
                            : $"return {fieldName};"),
                IsBodyExpression = isExpressionBodiedProperty || getAccessor?.ExpressionBody != null,
                IsAutoGenerated = true,
                IsExplicitInterfaceImplementation = isExplicitInterfaceImplementation
            };
            if (propertySymbol != null && JavaNaming.IsIncompatibleTypeHidingProperty(propertySymbol))
            {
                getter.Annotations.Add(new JavaAnnotation($"CompatPropertyName(\"{propertySymbol.Name}\")"));
                context.AddImport("io.github.ningpp.compat.CompatPropertyName");
            }

            // C# [JsonExtensionData] -> compat annotation; the symbol must be resolved from the
            // declaration because the propertySymbol parameter is null on this path.
            if (context.SemanticModel != null)
            {
                var extDataSymbol = context.GetDeclaredSymbol(propDecl);
                if (extDataSymbol?.GetAttributes().Any(a =>
                        a.AttributeClass?.Name == "JsonExtensionDataAttribute") == true)
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatJsonExtensionData"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonExtensionData");
                }
                // C# [JsonIgnore] -> compat annotation (same route: the generated attribute class
                // is materialized by compat from this marker; ConditionalIgnoreWhen/other ctor
                // arguments are not modelled yet).
                if (extDataSymbol?.GetAttributes().Any(a =>
                        a.AttributeClass?.Name == "JsonIgnoreAttribute") == true)
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatJsonIgnore"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonIgnore");
                }
            }

            // C# [DebuggerBrowsable(state)] on the property → runtime annotation on the
            if (context.SemanticModel != null)
            {
                var propSymbol = context.GetDeclaredSymbol(propDecl);
                // C# [JsonNumberHandling(flags)] on a member overrides the type's handling;
                // compat materializes the generated attribute class from this marker on the
                // member lookup (JsonNumberHandlingAttribute is a class, not an annotation).
                var numberHandlingAttr = propSymbol?.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "JsonNumberHandlingAttribute");
                if (numberHandlingAttr?.ConstructorArguments.Length > 0
                    && numberHandlingAttr.ConstructorArguments[0].Value != null)
                {
                    getter.Annotations.Add(new JavaAnnotation(
                        $"CompatJsonNumberHandling({numberHandlingAttr.ConstructorArguments[0].Value})"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonNumberHandling");
                }                // C# sbyte[] and byte[] are both byte[] in Java but serialize differently
                // (JSON array of numbers vs. Base64 string); mark the member so the compat
                // member lookup reports the integer element type.
                if (typeInfo.Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_SByte })
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatSByteArray"));
                    context.AddImport("io.github.ningpp.compat.CompatSByteArray");
                }
                var browsableAttr = propSymbol?.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "DebuggerBrowsableAttribute");
                if (browsableAttr?.ConstructorArguments.Length > 0
                    && browsableAttr.ConstructorArguments[0].Value is int browsableState)
                {
                    getter.Annotations.Add(new JavaAnnotation($"DebuggerBrowsable({browsableState})"));
                    context.AddImport("io.github.ningpp.compat.DebuggerBrowsable");
                }
                // C# [JsonInclude] -> compat annotation. The generated JsonIncludeAttribute is a
                // class, not an annotation, so the member lookup materializes it from this marker;
                // the SDK keys the "non-public member with [JsonInclude] is invalid" validation on
                // the attribute's presence.
                if (propSymbol?.GetAttributes().Any(a =>
                        a.AttributeClass?.Name == "JsonIncludeAttribute") == true)
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatJsonInclude"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonInclude");
                }
                // C# [JsonPropertyName("...")] -> compat annotation carrying the name. The
                // generated JsonPropertyNameAttribute is a class, not an annotation, so compat
                // materializes it from this marker; without the name the C# JSON property name
                // never reaches the runtime, no name collision is detected, and every test that
                // asserts InvalidOperationException on a collision fails ("No exception thrown").
                var propertyNameAttr = propSymbol?.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute");
                if (propertyNameAttr?.ConstructorArguments.Length > 0
                    && propertyNameAttr.ConstructorArguments[0].Value is string jsonPropertyName)
                {
                    getter.Annotations.Add(new JavaAnnotation(
                        $"CompatJsonPropertyName(\"{jsonPropertyName}\")"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonPropertyName");
                }
                // C# [JsonConverter(typeof(X))] -> compat annotation carrying the converter type.
                // The generated JsonConverterAttribute is a class, not an annotation, so compat
                // materializes it from this marker. Without it the attribute never reaches the
                // runtime and getConverterFromAttribute never runs, so the "does not derive from
                // JsonConverter / have a public parameterless constructor" and "is not compatible
                // with the type" validations are skipped entirely.
                if (TryGetJsonConverterType(propSymbol, out var converterTypeName))
                {
                    getter.Annotations.Add(new JavaAnnotation(
                        $"CompatJsonConverter({converterTypeName}.class)"));
                    context.AddImport("io.github.ningpp.compat.CompatJsonConverter");
                }
                // A property whose C# accessors are all non-public is invisible to .NET's member
                // cache, but the converted class still exposes the accessors as Java methods, so
                // the generated serializability test would accept it. Mark it so compat answers
                // with the C# semantics (PropertyVisibilityTests.privateSetter).
                if (propSymbol is IPropertySymbol declaredProp
                    && !IsPublicAccessor(declaredProp.GetMethod)
                    && !IsPublicAccessor(declaredProp.SetMethod))
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatNonPublicProperty"));
                    context.AddImport("io.github.ningpp.compat.CompatNonPublicProperty");
                }
                // The MIXED case: .NET keeps a property only when a PUBLIC accessor exists
                // (GetMethod?.IsPublic || SetMethod?.IsPublic), so a property with one non-public
                // accessor must be invisible on that side. The converted Java accessor is a public
                // method either way, so the fact has to be carried per accessor; without it
                // PropertyVisibilityTests.nonPublicMembersAreNotIncluded writes
                // {"MyInt":0,"MyString":null,...} where .NET writes {}.
                if (propSymbol is IPropertySymbol accessorProp
                    && !HasJsonInclude(accessorProp)
                    && !IsPublicAccessor(accessorProp.GetMethod))
                {
                    getter.Annotations.Add(new JavaAnnotation("CompatNonPublicAccessor"));
                    context.AddImport("io.github.ningpp.compat.CompatNonPublicAccessor");
                }
            }

            // Expression-bodied property getters skip TransformReturnStatement, so
            // Stream/Array wrapping for Iterable/Collection/List return types must be done here.
            if (getter.IsBodyExpression && getter.Body != null)
            {
                var csExpr = isExpressionBodiedProperty
                    ? propDecl.ExpressionBody!.Expression
                    : getAccessor?.ExpressionBody?.Expression;
                if (csExpr != null)
                {
                    if (isEncodingPreambleProperty && typeInfo.Type?.ToDisplayString() == "System.ReadOnlySpan<byte>")
                    {
                        context.AddImport("io.github.ningpp.compat.MemoryExtensions");
                        getter.Body = $"MemoryExtensions.asSpan({getter.Body})";
                    }

                    getter.Body = MethodTransformer.WrapExpressionBodyForIterableReturn(
                        getter.Body, csExpr, propDecl.Type, context);
                    getter.Body = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                        csExpr, getter.Body, typeInfo.Type, context);
                    getter.Body = StructCloneHelper.CloneStructValueIfNeeded(
                        csExpr,
                        getter.Body,
                        typeInfo.Type,
                        context);
                }

                // Expression-bodied getters can produce pre-statements during transformation
                // (e.g. lazy initialization via ??= hoists `if (f == null) f = ...`). Convert
                // to a block body so the statements stay inside the getter instead of leaking
                // into the following member's declaration.
                if (context.HasPendingPreStatements || context.HasPendingPostStatements)
                {
                    var bodyLines = new List<string>();
                    if (context.HasPendingPreStatements)
                    {
                        foreach (var pre in context.DrainPreStatements())
                            bodyLines.Add(pre.TrimEnd(';') + ";");
                    }
                    if (context.HasPendingPostStatements)
                    {
                        var retHolder = context.GenerateSyntheticName("_ret");
                        bodyLines.Add($"var {retHolder} = {getter.Body};");
                        foreach (var post in context.DrainPostStatements())
                            bodyLines.Add(post.TrimEnd(';') + ";");
                        bodyLines.Add($"return {retHolder};");
                    }
                    else
                    {
                        bodyLines.Add($"return {getter.Body.TrimEnd(';')};");
                    }
                    getter.Body = string.Join("\n", bodyLines);
                    getter.IsBodyExpression = false;
                }
            }

            // 处理显式 getter 主体
            if (getAccessor?.Body != null)
            {
                // Set ReturnsCSharpGenericIterable for the getter body processing
                context.ReturnsCSharpGenericIterable = getter.ReturnType != null
                    && getter.ReturnType.StartsWith("CSharpGenericIterable<");
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                // Detect yield-returning getter: convert to list accumulation pattern
                bool isYieldGetter = getAccessor.Body.DescendantNodes().OfType<YieldStatementSyntax>().Any();
                if (isYieldGetter)
                {
                    context.IsInYieldMethod = true;
                    context.AddImport("io.github.ningpp.compat.CSharpList");
                    var elemType = PropertyYieldExtractElementType(propType);
                    // Use CSharpGenericIterable<T> for IEnumerable<T> properties, List<T> otherwise
                    if (propType.StartsWith("CSharpGenericIterable<") || propType.StartsWith("CSharpICollection<") || propType.StartsWith("CSharpGenericIList<"))
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                        getter.ReturnType = $"CSharpGenericIterable<{elemType ?? "Object"}>";
                    }
                    else
                    {
                        context.AddImport("java.util.List");
                        getter.ReturnType = $"List<{elemType ?? "Object"}>";
                    }
                    var body = statementTransformer.TransformBlock(getAccessor.Body, context);
                    var listType = elemType != null ? $"CSharpList<{elemType}>" : "CSharpList<Object>";
                    getter.Body = $"{listType} _yieldResult = new {listType}();\n        {body}\n        return _yieldResult;";
                    context.IsInYieldMethod = false;
                }
                else
                {
                    getter.StructuredBody = statementTransformer.TransformBlockToStructuredBody(getAccessor.Body, context);
                }
                getter.IsBodyExpression = false;
            }

            results.Add(getter);
        }

        // 创建 setter
        if (ShouldEmitHidingAccessors(isHidingBaseMember, isPureBaseDelegation, propDecl, needsBackingField) && hasSetter)
        {
            var setterModifiers = modifiers;
            // 如果属性本身没有访问修饰符，默认为 public
            if (!propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) ||
                                          m.IsKind(SyntaxKind.ProtectedKeyword) ||
                                          m.IsKind(SyntaxKind.PrivateKeyword) ||
                                          m.IsKind(SyntaxKind.InternalKeyword)))
            {
                setterModifiers = JavaModifiers.Public | (modifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // If the accessor has an explicit access modifier, use that instead of the property-level one.
            // Only visibility modifiers count — `readonly set` must not strip the property visibility.
            if (setAccessor?.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword)
                    || m.IsKind(SyntaxKind.ProtectedKeyword)
                    || m.IsKind(SyntaxKind.InternalKeyword)) == true)
            {
                var accessorVisibility = ConvertModifiers(setAccessor.Modifiers, isStatic: false);
                setterModifiers = (setterModifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private))
                                | (accessorVisibility & (JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // 确保只有一个访问修饰符
            bool propertyIsPublic = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                || !propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword)
                    || m.IsKind(SyntaxKind.InternalKeyword));
            setterModifiers = GetSingleAccessModifier(
                setterModifiers,
                propertyIsPublic,
                context.Options.TargetLanguage == TargetLanguage.Java);

            string? setterBodyExpr = null;
            bool setterIsBodyExpr = false;
            if (setAccessor?.ExpressionBody != null)
            {
                setterBodyExpr = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(setAccessor.ExpressionBody.Expression, context);
                setterIsBodyExpr = true;

                // Expression-bodied setter that assigns to another property may produce
                // pre-statements (from HoistChainedPropertyAssignment). Convert to block body.
                if (context.HasPendingPreStatements || context.HasPendingPostStatements)
                {
                    var bodyLines = new List<JavaStatement>();
                    if (context.HasPendingPreStatements)
                    {
                        foreach (var pre in context.DrainPreStatements())
                            bodyLines.Add(new JavaRawStatement(pre.TrimEnd(';') + ";"));
                    }
                    // For void setters, a bare read expression (like _chainVal1 from hoisting)
                    // is not a valid Java statement — skip it.
                    bool isNonStatement = IsBareReadExpression(setterBodyExpr);
                    if (!isNonStatement)
                        bodyLines.Add(new JavaRawStatement(setterBodyExpr.TrimEnd(';') + ";"));
                    if (context.HasPendingPostStatements)
                    {
                        foreach (var post in context.DrainPostStatements())
                            bodyLines.Add(new JavaRawStatement(post.TrimEnd(';') + ";"));
                    }

                    var setter2 = new JavaMethodDeclaration
                    {
                        Name = "set" + ToPascalCase(propName) + GetPropertyHidingSuffix(propertySymbol),
                        ReturnType = "void",
                        Modifiers = setterModifiers,
                        LeadingComment = hasGetter ? null : propertyComments,
                        Parameters = { new JavaParameter(propType, "value") },
                        StructuredBody = new JavaMethodBody(bodyLines),
                        IsAutoGenerated = true
                    };
                    if (propertySymbol != null && JavaNaming.IsIncompatibleTypeHidingProperty(propertySymbol))
                    {
                        setter2.Annotations.Add(new JavaAnnotation($"CompatPropertyName(\"{propertySymbol.Name}\")"));
                        context.AddImport("io.github.ningpp.compat.CompatPropertyName");
                    }
                    // Detect C# 9 init-only setter and annotate
                    bool isInitSetter2 = setAccessor.IsKind(SyntaxKind.InitAccessorDeclaration);
                    if (isInitSetter2)
                        setter2.LeadingComment = ConvertedCommentSet.JoinComments(setter2.LeadingComment, "/** @implNote Set during construction only (C# init accessor). */");
                    results.Add(setter2);
                    goto AfterSetter;
                }
            }

            var setter = new JavaMethodDeclaration
            {
                Name = "set" + ToPascalCase(propName) + GetPropertyHidingSuffix(propertySymbol),
                ReturnType = "void",
                Modifiers = setterModifiers,
                LeadingComment = hasGetter ? null : propertyComments,
                Parameters = { new JavaParameter(propType, "value") },
                Body = setterBodyExpr
                    ?? (StructCloneHelper.IsUserDefinedStruct(typeInfo.Type)
                        ? (isStatic ? $"{fieldName} = value.clone();" : $"this.{fieldName} = value.clone();")
                        : (isStatic ? $"{fieldName} = value;" : $"this.{fieldName} = value;")),
                IsBodyExpression = setterIsBodyExpr,
                IsAutoGenerated = true,
                IsExplicitInterfaceImplementation = isExplicitInterfaceImplementation
            };
            if (propertySymbol != null && JavaNaming.IsIncompatibleTypeHidingProperty(propertySymbol))
            {
                setter.Annotations.Add(new JavaAnnotation($"CompatPropertyName(\"{propertySymbol.Name}\")"));
                context.AddImport("io.github.ningpp.compat.CompatPropertyName");
            }
            // The MIXED-visibility case: .NET keeps a property only when a PUBLIC accessor exists
            // (GetMethod?.IsPublic || SetMethod?.IsPublic), so a non-public setter must be
            // invisible to that side. The converted Java accessor is a public method either way,
            // so the fact has to be carried per accessor; without it
            // PropertyVisibilityTests.nonPublicMembersAreNotIncluded writes
            // {"MyInt":0,"MyString":null,...} where .NET writes {}.
            // [JsonInclude] OPTS the non-public accessor back in — that is exactly what the
            // attribute is for (PointWith_JsonSerializableProperties.X is
            // [JsonInclude] public int X { get; internal set; } and must round-trip).
            if (propertySymbol is IPropertySymbol setterProp
                && !HasJsonInclude(setterProp)
                && !IsPublicAccessor(setterProp.SetMethod))
            {
                setter.Annotations.Add(new JavaAnnotation("CompatNonPublicAccessor"));
                context.AddImport("io.github.ningpp.compat.CompatNonPublicAccessor");
            }

            // 处理显式 setter 主体
            if (setAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                setter.StructuredBody = statementTransformer.TransformBlockToStructuredBody(setAccessor.Body, context);
                setter.IsBodyExpression = false;
            }

            // Detect C# 9 init-only setter and annotate the generated Java setter
            bool isInitSetter = setAccessor?.IsKind(SyntaxKind.InitAccessorDeclaration) == true;
            if (isInitSetter)
                setter.LeadingComment = ConvertedCommentSet.JoinComments(setter.LeadingComment, "/** @implNote Set during construction only (C# init accessor). */");

            results.Add(setter);
        }

    AfterSetter:
        // Local functions declared inside property accessor bodies are hoisted to
        // class-level private methods (Java has no local functions), exactly like
        // local functions in regular methods.
        var propertyHoistedLocalFunctions = new Queue<LocalFunctionStatementSyntax>();
        foreach (var localFn in context.MethodState.DrainHoistedLocalFunctions())
            propertyHoistedLocalFunctions.Enqueue(localFn);
        while (propertyHoistedLocalFunctions.Count > 0)
        {
            var localFn = propertyHoistedLocalFunctions.Dequeue();
            results.Add(MethodTransformer.TransformLocalFunction(localFn, context));
            foreach (var nested in context.MethodState.DrainHoistedLocalFunctions())
                propertyHoistedLocalFunctions.Enqueue(nested);
        }

        // 返回包装的结果
        context.IsInStaticMember = previousStaticContext;
        return new JavaMemberCollection(results);
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, bool isStatic)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            // 使用 RawKind 而不是 Kind 属性来避免命名空间冲突
            var kind = (Microsoft.CodeAnalysis.CSharp.SyntaxKind)modifier.RawKind;
            result |= kind switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword => JavaModifiers.Static,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.VirtualKeyword => JavaModifiers.None,  // Java 默认 virtual
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.NewKeyword => JavaModifiers.Override,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SealedKeyword => JavaModifiers.Final,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        if (isStatic)
        {
            result |= JavaModifiers.Static;
        }

        // C# "protected internal" maps to Protected | Public — Java doesn't allow both; keep Protected.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        return result;
    }

    private JavaModifiers GetSingleAccessModifier(JavaModifiers modifiers, bool propertyIsPublic = false, bool javaTarget = true)
    {
        // C# 'private protected' yields BOTH Private and Protected flags; it means
        // accessible to the containing type and its derived classes — the closest
        // Java equivalent is protected so subclasses can call the setter.
        if ((modifiers & JavaModifiers.Private) != 0 && (modifiers & JavaModifiers.Protected) != 0)
            return (modifiers & ~JavaModifiers.Public & ~JavaModifiers.Private);
        // C# 'private set' on a public property: C# permits assignment from any
        // method of the declaring type, including to instances of derived classes
        // (e.g. a base-class method assigning a subclass instance's property). Java's
        // private member is not visible through a subclass reference, so widen a
        // private setter to protected — matching the private-protected precedent.
        // TypeScript keeps a plain private setter (its set accessor is private).
        if (javaTarget
            && propertyIsPublic
            && (modifiers & JavaModifiers.Private) != 0
            && (modifiers & JavaModifiers.Public) == 0
            && (modifiers & JavaModifiers.Protected) == 0)
            return (modifiers & ~JavaModifiers.Private) | JavaModifiers.Protected;
        // 确保只有一个访问修饰符（public, protected, private）
        // 优先级：private > protected > public
        if ((modifiers & JavaModifiers.Private) != 0)
            return (modifiers & ~JavaModifiers.Public & ~JavaModifiers.Protected);
        if ((modifiers & JavaModifiers.Protected) != 0)
            return (modifiers & ~JavaModifiers.Public);
        return modifiers;
    }

    private string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLower(name[0]) + name.Substring(1);
    }

    private string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private static string GetPropertyHidingSuffix(IPropertySymbol? propertySymbol)
        => propertySymbol != null && JavaNaming.IsIncompatibleTypeHidingProperty(propertySymbol)
            ? "_newSlot"
            : "";

    private static bool IsSystemTextEncodingType(ITypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Text.Encoding")
                return true;
        }

        return false;
    }

    private static string? PropertyYieldExtractElementType(string javaType)
    {
        var m = System.Text.RegularExpressions.Regex.Match(javaType,
            @"^(?:Iterable|Iterator|List|ArrayList|Collection|IEnumerable|CSharpGenericIterable|CSharpGenericEnumerator|CSharpICollection|CSharpGenericIList|CSharpList|CSharpEnumerator)<(.+)>$");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Whether a hiding property still gets its accessors generated.
    ///
    /// <p>The general rule is to skip them: C# hiding is non-virtual, so a base-typed reference
    /// must reach the base member, while Java methods are virtual
    /// ({@see IsHidingBaseMember}). Two cases keep their accessors:</p>
    /// <ul>
    ///   <li>a property that only delegates to {@code base.X} - its accessors explicitly call
    ///       {@code super} and therefore widen accessibility without changing dispatch;</li>
    ///   <li>an AUTO-property, whose body is just its own backing field. Skipping its accessors
    ///       leaves the field unreachable and the member missing altogether: for
    ///       <code>class ChildClass : BaseClass { public new bool IsTransient { get; set; } }</code>
    ///       the generated ChildClass had only {@code private boolean isTransient;} and the
    ///       inherited internal accessor was the sole member, so a member .NET serializes
    ///       disappeared (JsonSerializerTests.newProperty). Reading/writing the field cannot
    ///       introduce a dispatch difference, because the generated accessor touches no other
    ///       member.</li>
    /// </ul>
    /// </summary>
    private static bool ShouldEmitHidingAccessors(
        bool isHidingBaseMember,
        bool isPureBaseDelegation,
        PropertyDeclarationSyntax propDecl,
        bool needsBackingField)
    {
        if (!isHidingBaseMember)
        {
            return true;
        }
        return isPureBaseDelegation || (needsBackingField && IsAutoProperty(propDecl));
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax propDecl)
    {
        if (propDecl.AccessorList == null)
        {
            return false;
        }
        foreach (var accessor in propDecl.AccessorList.Accessors)
        {
            if (accessor.Body != null || accessor.ExpressionBody != null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Detects whether a hiding property's accessor bodies purely delegate to the base
    /// property (e.g. <c>public new T X { get => base.X; set => base.X = value; }</c>).
    /// Such properties exist solely to widen accessibility (e.g. protected -> public);
    /// they must still be emitted in Java as <c>super.getX()</c>/<c>super.setX(value)</c>
    /// so callers resolve to the public bridge instead of the inaccessible base member.
    /// Both expression-bodied (<c>get => base.X;</c>) and single-statement block-bodied
    /// (<c>get { return base.X; }</c>) forms are recognised.
    /// </summary>
    private static bool IsPureBaseDelegationProperty(PropertyDeclarationSyntax propDecl)
    {
        if (propDecl.AccessorList == null) return false;
        var propName = propDecl.Identifier.Text;

        var getAccessor = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        bool getterIsBaseDelegation = getAccessor == null || IsBasePropertyRead(getAccessor, propName);
        bool setterIsBaseDelegation = setAccessor == null || IsBasePropertyWrite(setAccessor, propName);
        return getterIsBaseDelegation && setterIsBaseDelegation;
    }

    private static bool IsBasePropertyRead(AccessorDeclarationSyntax accessor, string propName)
    {
        // get => base.PropName;
        if (accessor.ExpressionBody?.Expression is MemberAccessExpressionSyntax mas)
        {
            return IsBaseMemberAccess(mas, propName);
        }
        // get { return base.PropName; }
        if (accessor.Body?.Statements.Count == 1
            && accessor.Body.Statements[0] is ReturnStatementSyntax ret
            && ret.Expression is MemberAccessExpressionSyntax mas2)
        {
            return IsBaseMemberAccess(mas2, propName);
        }
        return false;
    }

    private static bool IsBasePropertyWrite(AccessorDeclarationSyntax accessor, string propName)
    {
        // set => base.PropName = value;
        if (accessor.ExpressionBody?.Expression is AssignmentExpressionSyntax assign
            && assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign.Right is IdentifierNameSyntax { Identifier: { Text: "value" } })
        {
            return assign.Left is MemberAccessExpressionSyntax mas && IsBaseMemberAccess(mas, propName);
        }
        // set { base.PropName = value; }
        if (accessor.Body?.Statements.Count == 1
            && accessor.Body.Statements[0] is ExpressionStatementSyntax es
            && es.Expression is AssignmentExpressionSyntax assign2
            && assign2.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign2.Right is IdentifierNameSyntax { Identifier: { Text: "value" } })
        {
            return assign2.Left is MemberAccessExpressionSyntax mas && IsBaseMemberAccess(mas, propName);
        }
        return false;
    }

    private static bool IsBaseMemberAccess(MemberAccessExpressionSyntax mas, string propName)
        => mas.Expression is BaseExpressionSyntax && mas.Name.Identifier.Text == propName;

    /// <summary>
    /// Checks whether a C# property hides a base class property (using `new` keyword or implicit hiding).
    /// In C#, hiding is non-virtual: base-typed references call the base getter.
    /// In Java, all methods are virtual, so we must skip generating getters/setters for hiding
    /// properties to preserve C# dispatch semantics.
    /// Properties that hide a base member but return a more derived type are still emitted,
    /// because callers inside the derived class rely on the derived return type
    /// (e.g. CTestModule.Attribute → TestModule).
    /// </summary>
    private static bool IsHidingBaseMember(ISymbol? propertySymbol)
    {
        if (propertySymbol is not IPropertySymbol prop) return false;
        if (prop.IsOverride) return false;  // explicit override is not hiding
        if (prop.IsStatic) return false;    // static members don't participate in hiding

        var baseType = prop.ContainingType.BaseType;
        while (baseType != null)
        {
            foreach (var member in baseType.GetMembers(prop.Name))
            {
                // Only non-private members are hidden (private members are not visible to derived classes)
                if (member.DeclaredAccessibility != Accessibility.Private
                    && member is IPropertySymbol baseProp)
                {
                    // Only treat as hiding when the return type is the same.
                    // Properties that return a more derived type are still needed
                    // so callers can access derived-type members.
                    if (SymbolEqualityComparer.Default.Equals(prop.Type, baseProp.Type))
                        return true;
                }
            }
            baseType = baseType.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Checks whether an expression string is a bare read expression that is not
    /// a valid Java statement on its own (e.g. "_chainVal1" from property-assignment hoisting).
    /// </summary>
    private static bool IsBareReadExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var trimmed = expr.Trim();
        return trimmed.EndsWith(".value", StringComparison.Ordinal)
            || trimmed.Split('.').All(IsIdentifierLikeSegment);
    }

    private static bool IsIdentifierLikeSegment(string segment)
    {
        return segment.Length > 0
            && (char.IsLetter(segment[0]) || segment[0] == '_' || segment[0] == '$')
            && segment.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
    }

    /// <summary>
    /// Translates C# XML serialization attributes on a property into the equivalent
    /// Java annotations on the generated backing field.
    /// </summary>
    private static void ApplyXmlSerializationAnnotations(PropertyDeclarationSyntax propDecl, JavaFieldDeclaration field, ConversionContext context)
    {
        foreach (var attrList in propDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var symbolInfo = context.GetSymbolInfo(attr);
                var attrType = symbolInfo.Symbol is IMethodSymbol ctor
                    ? ctor.ContainingType?.ToDisplayString()
                    : symbolInfo.Symbol?.ContainingType?.ToDisplayString();

                if (attrType is null)
                    continue;

                var args = attr.ArgumentList?.Arguments;
                var firstArg = args is { Count: > 0 } ? args.Value[0].Expression.ToString() : null;

                switch (attrType)
                {
                    case "System.Xml.Serialization.XmlAttributeAttribute":
                        context.AddImport("io.github.ningpp.compat.xml.XmlAttribute");
                        var xmlAttr = new JavaAnnotation("XmlAttribute");
                        if (firstArg != null)
                            xmlAttr.Values["name"] = firstArg;
                        field.Annotations.Add(xmlAttr);
                        break;

                    case "System.Xml.Serialization.XmlIgnoreAttribute":
                        context.AddImport("io.github.ningpp.compat.xml.XmlIgnore");
                        field.Annotations.Add(new JavaAnnotation("XmlIgnore"));
                        break;

                    case "System.Xml.Serialization.XmlArrayAttribute":
                        context.AddImport("io.github.ningpp.compat.xml.XmlArray");
                        var xmlArray = new JavaAnnotation("XmlArray");
                        if (firstArg != null)
                            xmlArray.Values["elementName"] = firstArg;
                        field.Annotations.Add(xmlArray);
                        break;

                    case "System.Xml.Serialization.XmlArrayItemAttribute":
                        context.AddImport("io.github.ningpp.compat.xml.XmlArrayItem");
                        var xmlArrayItem = new JavaAnnotation("XmlArrayItem");
                        if (firstArg != null)
                            xmlArrayItem.Values["elementName"] = firstArg;
                        field.Annotations.Add(xmlArrayItem);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Overrides of generic base properties (e.g. bool Key over TKey Key in
    /// DictionaryKeyTestsBase&lt;TKey, TValue&gt;) must return the boxed wrapper —
    /// boolean getKey() does not override Object/TKey getKey().
    /// </summary>
    private static string GetOverrideAwarePropertyType(
        PropertyDeclarationSyntax propDecl,
        string propType,
        ConversionContext context)
    {
        if (propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword))
            && (context.GetDeclaredSymbol(propDecl) as IPropertySymbol)?.OverriddenProperty is IPropertySymbol overriddenProp
            && overriddenProp.OriginalDefinition.Type is ITypeParameterSymbol)
        {
            return ExpressionTransformerHelpers.BoxJavaPrimitiveType(propType);
        }
        return propType;
    }
    private static string BuildEnumTypeReference(INamedTypeSymbol enumType)
    {
        return enumType.ContainingType is INamedTypeSymbol parentType
            ? $"{BuildEnumTypeReference(parentType)}.{enumType.Name}"
            : enumType.Name;
    }

    private static string? FindEnumMemberByValue(INamedTypeSymbol enumType, long value)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } field)
            {
                if (field.ConstantValue is int intVal && intVal == value)
                    return field.Name;
                if (field.ConstantValue is long longVal && longVal == value)
                    return field.Name;
                if (field.ConstantValue is short shortVal && shortVal == value)
                    return field.Name;
                if (field.ConstantValue is byte byteVal && byteVal == value)
                    return field.Name;
                if (field.ConstantValue is sbyte sbyteVal && sbyteVal == value)
                    return field.Name;
                if (field.ConstantValue is ushort ushortVal && ushortVal == value)
                    return field.Name;
                if (field.ConstantValue is uint uintVal && uintVal == value)
                    return field.Name;
                if (field.ConstantValue is ulong ulongVal && ulongVal == (ulong)value)
                    return field.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// C# accessor visibility: an accessor counts as public only when declared public.
    /// .NET caches a property only when {@code GetMethod?.IsPublic || SetMethod?.IsPublic}, so a
    /// property whose accessors are all non-public is invisible to the serializer.
    /// </summary>
    private static bool IsPublicAccessor(IMethodSymbol? accessor)
        => accessor != null && accessor.DeclaredAccessibility == Accessibility.Public;

    /// <summary>
    /// Resolves the converter type a C# <c>[JsonConverter]</c> names, following a DERIVED attribute
    /// class to the base that actually holds it.
    /// </summary>
    /// <remarks>
    /// C# lets a test or library declare its own attribute:
    /// <code>class InvalidConverterAttribute : JsonConverterAttribute { public
    /// InvalidConverterAttribute() : base(typeof(int)) { } }</code> and then write
    /// <c>[InvalidConverter]</c>. The converter type is a constructor argument of the BASE class, so
    /// reading only the attribute the source wrote finds nothing. Walking up the inheritance chain
    /// is what makes both the validation tests and the plain <c>[JsonConverter(typeof(X))]</c> cases
    /// resolve to the same runtime-visible value.
    /// </remarks>
    internal static bool TryGetJsonConverterType(ISymbol? member, out string converterTypeName)
    {
        converterTypeName = string.Empty;
        if (member == null)
            return false;
        foreach (var attribute in member.GetAttributes())
        {
            for (var attributeClass = attribute.AttributeClass;
                 attributeClass != null;
                 attributeClass = attributeClass.BaseType)
            {
                if (attributeClass.Name != "JsonConverterAttribute")
                    continue;
                // (a) Written directly: [JsonConverter(typeof(X))] puts the type in the attribute's
                // own construction.
                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument.Value is INamedTypeSymbol named)
                    {
                        converterTypeName = named.Name;
                        return true;
                    }
                }
                // (b) Written through a DERIVED attribute:
                //     class InvalidConverterAttribute : JsonConverterAttribute
                //     { public InvalidConverterAttribute() : base(typeof(int)) { } }
                // The type lives in the derived class's constructor initializer, so the attribute's
                // own arguments are empty and the declaration has to be read.
                if (TryGetConverterTypeFromConstructorInitializer(attribute.AttributeClass, out converterTypeName))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads {@code base(typeof(X))} out of a derived attribute class's constructor.
    /// </summary>
    private static bool TryGetConverterTypeFromConstructorInitializer(
        INamedTypeSymbol? attributeClass, out string converterTypeName)
    {
        converterTypeName = string.Empty;
        if (attributeClass == null)
            return false;
        foreach (var syntaxReference in attributeClass.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not TypeDeclarationSyntax typeDeclaration)
                continue;
            foreach (var constructor in typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                var initializer = constructor.Initializer;
                if (initializer == null
                    || !initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
                {
                    continue;
                }
                foreach (var argument in initializer.ArgumentList.Arguments)
                {
                    if (argument.Expression is TypeOfExpressionSyntax typeOf)
                    {
                        converterTypeName = typeOf.Type.ToString();
                        // Strip any namespace qualification: the annotation names the Java class,
                        // which the generated file imports.
                        var lastDot = converterTypeName.LastIndexOf('.');
                        if (lastDot >= 0)
                            converterTypeName = converterTypeName[(lastDot + 1)..];
                        return converterTypeName.Length > 0;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the property carries C# <c>[JsonInclude]</c>, which opts a non-public accessor back
    /// into (de)serialization.
    /// </summary>
    private static bool HasJsonInclude(IPropertySymbol property)
        => property.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIncludeAttribute");
}

