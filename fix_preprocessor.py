with open('src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs', 'r') as f:
    content = f.read()

# === Fix conflict 1 ===
old1 = "<<<<<<< HEAD\n        var propertyPlans = active.OfType<Properties.PropertyPlan>().ToList();\n        var eventPlans = active.OfType<Events.EventPlan>().ToList();\n        var indexerPlans = active.OfType<Indexers.IndexerPlan>().ToList();\n        var plannedProperties = propertyPlans\n            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();\n        var plannedEvents = eventPlans\n            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();\n        var plannedIndexers = indexerPlans\n            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();\n        if (plannedProperties.Count == 0 && plannedEvents.Count == 0 && plannedIndexers.Count == 0)\n=======\n        var plannedProps = active.OfType<Properties.PropertyPlan>()\n            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();\n        var plannedOps = active.OfType<Operators.OperatorPlan>()\n            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();\n        // Info 级白名单（inc/dec、门控 &/|）在重写期计数：文件含 operator 声明时\n        // 即使无任何 plan 也过一遍 operator rewriter（树不变 -> 计数后按 clean 跳过）。\n        var hasOperatorDecls = root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Any();\n        if (plannedProps.Count == 0 && plannedOps.Count == 0 && !hasOperatorDecls)\n>>>>>>> c5fd759b"

new1 = """        var propertyPlans = active.OfType<Properties.PropertyPlan>().ToList();
        var eventPlans = active.OfType<Events.EventPlan>().ToList();
        var indexerPlans = active.OfType<Indexers.IndexerPlan>().ToList();
        var operatorPlans = active.OfType<Operators.OperatorPlan>().ToList();
        var plannedProperties = propertyPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedEvents = eventPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedIndexers = indexerPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedOperators = operatorPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        // Info 级白名单（inc/dec、门控 &/|）在重写期计数：文件含 operator 声明时
        // 即使无任何 plan 也过一遍 operator rewriter（树不变 -> 计数后按 clean 跳过）。
        var hasOperatorDecls = root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Any();
        if (plannedProperties.Count == 0 && plannedEvents.Count == 0
            && plannedIndexers.Count == 0 && plannedOperators.Count == 0 && !hasOperatorDecls)"""

content = content.replace(old1, new1)

# === Fix conflict 3 ===
old3 = "<<<<<<< HEAD\n                MemberDesugarSeverity.Info,\n                isEvent ? MemberDesugarDiagnosticCode.EventPlanCreated : MemberDesugarDiagnosticCode.PlanCreated,\n                isEvent ? \"event plan skipped by overrides.skip configuration\"\n                        : \"property plan skipped by overrides.skip configuration\",\n=======\n                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.PlanCreated,\n                \"member-desugar plan skipped by overrides.skip configuration\",\n>>>>>>> c5fd759b"

new3 = """                MemberDesugarSeverity.Info,
                isEvent ? MemberDesugarDiagnosticCode.EventPlanCreated : MemberDesugarDiagnosticCode.PlanCreated,
                isEvent ? "event plan skipped by overrides.skip configuration"
                        : "property plan skipped by overrides.skip configuration","""

content = content.replace(old3, new3)

# === Fix conflict 2 (full dispatcher) ===
old2_start = "<<<<<<< HEAD\n        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));\n        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));\n        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));\n\n        // \u5c5e\u6027\u2192\u4e8b\u4ef6(\u91cd\u89e3\u6790)\u2192\u7d22\u5f15\u5668\u4e09\u7ea7 dispatch"

old2_end = "        if (workingRoot.IsEquivalentTo(root))\n=======\n        statistics.PropertiesPlanned += plannedProps.Count(p => IsFirstDeclarationFile(p, filePath));\n"

# Find the section more carefully
idx_start = content.find("<<<<<<< HEAD\n        statistics.PropertiesPlanned += plannedProperties.Count")
idx_end = content.find(">>>>>>> c5fd759b (feat(member-desugar): C14 operator rule/rewriter")

if idx_start > 0 and idx_end > 0:
    # Find the line after >>>>>>>
    newline_after = content.index('\n', idx_end) + 1
    old2 = content[idx_start:newline_after]
    
    new2 = """        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOperators.Count(p => IsFirstDeclarationFile(p, filePath));

        // 四级 dispatch：属性->事件(重解析)->运算符(重解析)->索引器
        // 属性改写器在原始树上运行；事件/运算符改写器经替换树运行（属性产物是红色节点，
        // 语义查询必须绑定自有模型）；索引器最后。事件 plan 归属键使用「含类型::事件名」显示串，
        // 运算符 plan 归属键使用「含类型::op 名(参数):返回」（设计 §4.6）。
        var workingRoot = root;
        if (plannedProperties.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProperties, propertyPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)rewriter.Visit(workingRoot)!;
        }
        if (plannedEvents.Count > 0)
        {
            // 属性改写产物的合成 token 间距由最终 Formatter 修复；重解析前必须先格式化，
            // 否则无空格相邻 token（如 private int / void add_E）会融合成非法标识符。
            using (var workspace = new AdhocWorkspace())
                workingRoot = (CompilationUnitSyntax)Formatter.Format(workingRoot, workspace);
            var eventTree = CSharpSyntaxTree.ParseText(
                workingRoot.ToFullString(), (CSharpParseOptions)semanticModel.SyntaxTree.Options, path: filePath);
            var eventCompilation = ((CSharpCompilation)semanticModel.Compilation)
                .ReplaceSyntaxTree(semanticModel.SyntaxTree, eventTree);
            var eventModel = eventCompilation.GetSemanticModel(eventTree);
            var eventRewriter = new Events.EventUseSiteRewriter(
                eventModel, plannedEvents, eventPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)eventRewriter.Visit(eventTree.GetRoot())!;
        }
        if (plannedOperators.Count > 0 || hasOperatorDecls)
        {
            // 事件/属性产物含无分隔 trivia 的合成节点（文本级 publicint/returnp），必须先经
            // 格式化+本地校验取干净文本，再解析回编译换取可绑定的新鲜模型。
            var intermediate = FormatAndValidate(workingRoot);
            if (intermediate == null)
            {
                statistics.FilesSkippedClean++;
                diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                    "member-desugar rewrite failed local parse validation; the original file was " +
                    "preserved pending whole-tree validation.", filePath, 0, 0, null));
                return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
            }
            SemanticModel operatorModel;
            if (ReferenceEquals(workingRoot, root))
            {
                operatorModel = semanticModel;
            }
            else
            {
                var (replacementModel, replacementRoot) = ParseReplacement(semanticModel, intermediate);
                operatorModel = replacementModel;
                workingRoot = replacementRoot;
            }
            var opRewriter = new Operators.OperatorUseSiteRewriter(
                operatorModel, plannedOperators, operatorPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)opRewriter.Visit(workingRoot)!;
        }
        if (plannedIndexers.Count > 0)
        {
            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                semanticModel, plannedIndexers, indexerPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)indexerRewriter.Visit(workingRoot)!;
        }
        if (workingRoot.IsEquivalentTo(root))
"""
    content = content[:idx_start] + new2 + content[newline_after:]

with open('src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs', 'w') as f:
    f.write(content)
print('Done')
