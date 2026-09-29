using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// C11 property 规则（Kind=Property, Order=10）。三步：
/// 1) 声明级闸门（partial/ref/static abstract/field 关键字/位置 record/特性路由）；
/// 2) 站点扫描（读/写/复合/++--/初始化器/with/表达式树/dynamic）+ 层次联动文件；
/// 3) 后备字段命名 + PropertyPlan 产出（设计 §4.4 PD-1..PD-12）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Property, 10)]
internal sealed class PropertyRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Property;
    public int Order => 10;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options;

        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        var candidates = new Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)>(
            SymbolEqualityComparer.Default);
        var failures = new Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } prop || prop.IsIndexer) continue;
                if (candidates.ContainsKey(prop) || failures.ContainsKey(prop)
                    || userSkipped.Contains(prop)) continue;

                if (MatchesSet(options.SkipQualifiedNames, prop))
                {
                    userSkipped.Add(prop);
                    continue;
                }

                var gate = DeclarationGate(decl, prop, model);
                if (gate != null)
                {
                    failures[prop] = gate.Value;
                    continue;
                }
                candidates[prop] = (decl, model);
            }
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IPropertySymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var candidateNames = candidates.Keys.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            var liftableMemo = new Dictionary<SyntaxNode, bool>();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assign when assign.Parent is InitializerExpressionSyntax:
                        HandleInitializerAssignment(assign, model, filePath, candidates,
                            siteFiles, siteFailures, liftableMemo);
                        break;

                    case AssignmentExpressionSyntax assign:
                        HandleStatementAssignment(assign, model, filePath, candidates, siteFiles, siteFailures);
                        break;

                    case PostfixUnaryExpressionSyntax post
                        when post.IsKind(SyntaxKind.PostIncrementExpression)
                            || post.IsKind(SyntaxKind.PostDecrementExpression):
                        HandleIncrement(post.Operand, post.Parent, model, filePath, candidates, siteFiles, siteFailures);
                        break;

                    case PrefixUnaryExpressionSyntax pre
                        when pre.IsKind(SyntaxKind.PreIncrementExpression)
                            || pre.IsKind(SyntaxKind.PreDecrementExpression):
                        HandleIncrement(pre.Operand, pre.Parent, model, filePath, candidates, siteFiles, siteFailures);
                        break;

                    case MemberAccessExpressionSyntax ma
                        when candidateNames.Contains(ma.Name.Identifier.ValueText):
                        HandleReadCandidate(ma, model, filePath, candidates, siteFiles, siteFailures, options);
                        break;

                    case MemberBindingExpressionSyntax mb
                        when candidateNames.Contains(mb.Name.Identifier.ValueText):
                        HandleReadCandidate(mb, model, filePath, candidates, siteFiles, siteFailures, options);
                        break;

                    case IdentifierNameSyntax id
                        when candidateNames.Contains(id.Identifier.ValueText):
                        HandleReadCandidate(id, model, filePath, candidates, siteFiles, siteFailures, options);
                        break;

                    case MemberAccessExpressionSyntax dyn
                        when MemberDesugarSyntaxFacts.IsDynamic(dyn, model)
                            && candidateNames.Contains(dyn.Name.Identifier.ValueText):
                        // dynamic 接收者（即使未绑定符号）：同名候选全部保守跳过（CS2J5118）
                        foreach (var c in candidates.Keys
                                     .Where(c => c.Name == dyn.Name.Identifier.ValueText).ToList())
                            MarkFailure(siteFailures, c,
                                MemberDesugarDiagnosticCode.DynamicSite, PropertyDiagnostics.Dynamic(c.Name));
                        break;
                }
            }
        }

        // ---- 阶段 3：后备字段命名 + plan 产出 ----
        var fieldNames = BackingFieldNaming.Assign(
            candidates.Where(kv => siteFailures.ContainsKey(kv.Key) == false
                                   && PropertyFacts.NeedsBackingField(kv.Value.Decl, kv.Key))
                .Select(kv => kv.Key));

        foreach (var (prop, declModel) in candidates)
        {
            var (decl, model) = declModel;
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in prop.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            if (siteFiles.TryGetValue(prop, out var files))
                foreach (var f in files) touched.Add(f);
            CollectHierarchyLinks(prop, context.SourceProperties, touched);

            if (userSkipped.Contains(prop)) continue; // 不可达（userSkipped 不入 candidates）

            if (siteFailures.TryGetValue(prop, out var fail))
            {
                builder.Add(new PropertyPlan
                {
                    Id = PropertyPlan.BuildId(prop),
                    Kind = MemberDesugarKind.Property,
                    Symbol = prop,
                    Outcome = MemberDesugarOutcome.Infeasible,
                    TouchedFiles = DeclarationFiles(prop),
                    FailCode = fail.Code,
                    FailReason = fail.Reason,
                    Property = prop,
                    GetterMethodName = AccessorsToMethods.GetterName(prop.Name),
                    SetterMethodName = AccessorsToMethods.SetterName(prop.Name),
                    HasSetter = prop.SetMethod != null,
                    HasInitAccessor = PropertyFacts.HasInitAccessor(decl),
                    IsRequired = PropertyFacts.IsRequired(decl),
                });
                continue;
            }

            fieldNames.TryGetValue(prop, out var fieldName);
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.Planned,
                TouchedFiles = touched.ToList(),
                Property = prop,
                BackingFieldName = fieldName,
                GetterMethodName = AccessorsToMethods.GetterName(prop.Name),
                SetterMethodName = AccessorsToMethods.SetterName(prop.Name),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = PropertyFacts.HasInitAccessor(decl),
                IsRequired = PropertyFacts.IsRequired(decl),
            });
        }

        foreach (var prop in userSkipped)
        {
            builder.Add(new PropertyPlan
            {
                Id = PropertyPlan.BuildId(prop),
                Kind = MemberDesugarKind.Property,
                Symbol = prop,
                Outcome = MemberDesugarOutcome.UserSkipped,
                TouchedFiles = DeclarationFiles(prop),
                Property = prop,
                GetterMethodName = AccessorsToMethods.GetterName(prop.Name),
                SetterMethodName = AccessorsToMethods.SetterName(prop.Name),
                HasSetter = prop.SetMethod != null,
                HasInitAccessor = false,
                IsRequired = false,
            });
        }
    }

    // ---- 声明级闸门（确定性顺序：5114 → 5115 → 5116 → 5122 → 5123 → 5113）----

    private static (MemberDesugarDiagnosticCode Code, string Reason)? DeclarationGate(
        PropertyDeclarationSyntax decl, IPropertySymbol prop, SemanticModel model)
    {
        if (PropertyFacts.IsPartial(decl))
            return (MemberDesugarDiagnosticCode.PartialProperty, PropertyDiagnostics.PartialProperty(prop.Name));
        if (PropertyFacts.ReturnsByRef(prop))
            return (MemberDesugarDiagnosticCode.RefReturningProperty, PropertyDiagnostics.RefReturning(prop.Name));
        if (PropertyFacts.IsStaticAbstractInterfaceMember(prop))
            return (MemberDesugarDiagnosticCode.StaticAbstractInterfaceProperty,
                PropertyDiagnostics.StaticAbstractInterface(prop.Name));
        if (PropertyFacts.UsesFieldKeyword(decl))
            return (MemberDesugarDiagnosticCode.FieldKeywordAccessor, PropertyDiagnostics.FieldKeyword(prop.Name));
        if (PropertyFacts.IsPositionalRecordProperty(prop))
            return (MemberDesugarDiagnosticCode.PositionalRecordProperty,
                PropertyDiagnostics.PositionalRecord(prop.Name));

        // 特性路由闸门（PD-10 / 决策 14）
        var fieldGenerated = PropertyFacts.NeedsBackingField(decl, prop) && !prop.IsAbstract;
        foreach (var list in decl.AttributeLists)
        {
            if (AttributeRouting.RouteList(list, model, fieldGenerated)
                == AttributeRouting.Destination.Unroutable)
            {
                var attrName = list.Attributes.FirstOrDefault()?.Name.ToString() ?? "<attribute>";
                return (MemberDesugarDiagnosticCode.AttributeUnroutable,
                    PropertyDiagnostics.AttributeUnroutable(prop.Name, attrName));
            }
        }
        return null;
    }

    // ---- 站点处理 ----

    private static void HandleInitializerAssignment(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        Dictionary<SyntaxNode, bool> liftableMemo)
    {
        if (!TryCandidate(assign.Left, model, candidates, out var prop)) return;

        if (assign.Ancestors().OfType<WithExpressionSyntax>().Any())
        {
            Record(siteFiles, prop, filePath);
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.WithSite,
                PropertyDiagnostics.WithSite(prop.Name));
            return;
        }

        Record(siteFiles, prop, filePath);
        var enclosing = assign.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        var liftable = enclosing != null && ComputeLiftable(enclosing, liftableMemo);
        if (!liftable)
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
    }

    private static void HandleStatementAssignment(
        AssignmentExpressionSyntax assign, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures)
    {
        // 元组/括号解构左值中的属性目标不可提升（保守，CS2J5119）
        if (assign.Left is TupleExpressionSyntax or ParenthesizedExpressionSyntax)
        {
            foreach (var expr in assign.Left.DescendantNodes().OfType<ExpressionSyntax>())
                if (TryCandidate(expr, model, candidates, out var p))
                {
                    Record(siteFiles, p, filePath);
                    MarkFailure(siteFailures, p, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                        PropertyDiagnostics.NonLiftable(p.Name));
                }
            return;
        }

        if (!TryCandidate(assign.Left, model, candidates, out var prop)) return;
        Record(siteFiles, prop, filePath);

        if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign.Parent is not ExpressionStatementSyntax)
        {
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
        }
    }

    private static void HandleIncrement(
        ExpressionSyntax operand, SyntaxNode? parent, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures)
    {
        if (!TryCandidate(operand, model, candidates, out var prop)) return;
        Record(siteFiles, prop, filePath);
        if (parent is not ExpressionStatementSyntax)
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.NonLiftableInitializer,
                PropertyDiagnostics.NonLiftable(prop.Name));
    }

    private static void HandleReadCandidate(
        ExpressionSyntax expr, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (!TryCandidate(expr, model, candidates, out var prop)) return;
        Record(siteFiles, prop, filePath);

        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(expr, model)
            && !MatchesSet(options.ForceQualifiedNames, prop))
        {
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.ExpressionTreeSite,
                PropertyDiagnostics.ExpressionTree(prop.Name));
            return;
        }
        if (MemberDesugarSyntaxFacts.IsDynamic(expr, model))
            MarkFailure(siteFailures, prop, MemberDesugarDiagnosticCode.DynamicSite,
                PropertyDiagnostics.Dynamic(prop.Name));
    }

    /// <summary>对象创建的可提升性：语句位（表达式语句/单变量局部声明/块内 return），
    /// 或作为可提升初始化器赋值的直接值继承（嵌套创建递归记忆化）。</summary>
    private static bool ComputeLiftable(ObjectCreationExpressionSyntax oc, Dictionary<SyntaxNode, bool> memo)
    {
        if (memo.TryGetValue(oc, out var cached)) return cached;
        memo[oc] = false; // 环路保护（语法树无环，保险）

        bool result;
        if (IsLiftablePosition(oc))
        {
            result = true;
        }
        else if (oc.Parent is EqualsValueClauseSyntax eq
            && eq.Parent is AssignmentExpressionSyntax a
            && a.Parent is InitializerExpressionSyntax init
            && init.Parent is ObjectCreationExpressionSyntax outer)
        {
            result = ComputeLiftable(outer, memo);
        }
        else if (oc.Parent is InitializerExpressionSyntax complex
            && complex.IsKind(SyntaxKind.ComplexElementInitializerExpression)
            && complex.Parent is EqualsValueClauseSyntax eq2
            && eq2.Parent is AssignmentExpressionSyntax a2
            && a2.Parent is InitializerExpressionSyntax init2
            && init2.Parent is ObjectCreationExpressionSyntax outer2)
        {
            result = ComputeLiftable(outer2, memo);
        }
        else
        {
            result = false;
        }

        memo[oc] = result;
        return result;
    }

    private static bool IsLiftablePosition(ObjectCreationExpressionSyntax oc)
    {
        switch (oc.Parent)
        {
            case ExpressionStatementSyntax:
                return true;
            case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax d
                && d.Parent is VariableDeclarationSyntax vd
                && vd.Variables.Count == 1
                && vd.Parent is LocalDeclarationStatementSyntax l
                && l.UsingKeyword.IsKind(SyntaxKind.None)
                && l.AwaitKeyword.IsKind(SyntaxKind.None)
                && !l.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
                    || m.IsKind(SyntaxKind.StaticKeyword)):
                return true;
            case ReturnStatementSyntax r when r.Parent is BlockSyntax or SwitchSectionSyntax:
                return true;
            default:
                return false;
        }
    }

    // ---- 层次联动（决策 8 + 回退闭环）：override 链 / 接口实现 ----

    private static void CollectHierarchyLinks(
        IPropertySymbol prop, IReadOnlyList<IPropertySymbol> all, SortedSet<string> files)
    {
        foreach (var other in all)
        {
            if (SymbolEqualityComparer.Default.Equals(other, prop)) continue;
            if (InOverrideChain(other, prop) || InOverrideChain(prop, other)
                || other.ExplicitInterfaceImplementations.Contains(prop, SymbolEqualityComparer.Default)
                || ImplementsImplicit(other, prop))
            {
                foreach (var loc in other.Locations.Where(l => l.IsInSource))
                    files.Add(loc.SourceTree!.FilePath);
            }
        }
    }

    private static bool InOverrideChain(IPropertySymbol derived, IPropertySymbol potentialBase)
    {
        for (var b = derived.OverriddenProperty; b != null; b = b.OverriddenProperty)
            if (SymbolEqualityComparer.Default.Equals(b, potentialBase)) return true;
        return false;
    }

    private static bool ImplementsImplicit(IPropertySymbol impl, IPropertySymbol ifaceProp)
        => ifaceProp.ContainingType is { TypeKind: TypeKind.Interface }
           && impl.ContainingType != null
           && SymbolEqualityComparer.Default.Equals(
               impl.ContainingType.FindImplementationForInterfaceMember(ifaceProp), impl);

    // ---- 小工具 ----

    internal static bool TryCandidate(
        ExpressionSyntax node, SemanticModel model,
        Dictionary<IPropertySymbol, (PropertyDeclarationSyntax Decl, SemanticModel Model)> candidates,
        out IPropertySymbol prop)
    {
        prop = null!;
        if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax))
            return false;
        if (model.GetSymbolInfo(node).Symbol is IPropertySymbol p && candidates.ContainsKey(p))
        {
            prop = p;
            return true;
        }
        return false;
    }

    private static void Record(
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles, IPropertySymbol prop, string filePath)
    {
        if (!siteFiles.TryGetValue(prop, out var set))
            siteFiles[prop] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IPropertySymbol prop, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(prop, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IPropertySymbol prop)
    {
        var type = prop.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{prop.Name}")
            || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IPropertySymbol prop)
        => prop.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>PD-10 / 决策 14：按 AttributeUsage 的目标感知路由。</summary>
internal static class AttributeRouting
{
    public enum Destination
    {
        Field,
        Methods,
        Getter,
        Unroutable,
    }

    public static Destination RouteList(AttributeListSyntax list, SemanticModel model, bool fieldGenerated)
    {
        var target = list.Target?.Identifier.ValueText;
        return target switch
        {
            "field" => fieldGenerated ? Destination.Field : RouteByUsage(list, model, allowField: false),
            "method" => Destination.Methods,
            "return" => Destination.Getter,
            _ => RouteByUsage(list, model, allowField: fieldGenerated),
        };
    }

    private static Destination RouteByUsage(AttributeListSyntax list, SemanticModel model, bool allowField)
    {
        var allField = list.Attributes.Count > 0 && allowField;
        var anyMethod = false;
        foreach (var attr in list.Attributes)
        {
            var usage = GetValidOn(model, attr);
            if (!usage.HasFlag(AttributeTargets.Field)) allField = false;
            if (usage.HasFlag(AttributeTargets.Method)) anyMethod = true;
        }
        if (allField) return Destination.Field;
        if (anyMethod) return Destination.Methods;
        return Destination.Unroutable;
    }

    public static AttributeTargets GetValidOn(SemanticModel model, AttributeSyntax attr)
    {
        var attrClass = (model.GetSymbolInfo(attr).Symbol as IMethodSymbol)?.ContainingType;
        if (attrClass == null) return AttributeTargets.All;
        var usage = attrClass.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { Name: "AttributeUsageAttribute" }
            && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "System");
        if (usage != null && usage.ConstructorArguments.Length > 0
            && usage.ConstructorArguments[0].Value is int v)
            return (AttributeTargets)v;
        return AttributeTargets.All;
    }
}
