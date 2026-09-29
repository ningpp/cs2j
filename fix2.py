with open('src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs', 'r') as f:
    content = f.read()

old = '''<<<<<<< HEAD
        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));

        // 属性→事件(重解析)→索引器三级 dispatch（C13 §3.2 + C12 Order）：
        // 属性改写器在原始树上运行；事件改写器在第二阶段经重解析树运行（属性产物是红色节点，
        // 事件语义查询必须绑定自有模型）；索引器最后。事件 plan 归属键 = 「含类型::事件名」显示串。
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
        if (plannedIndexers.Count > 0)
        {
            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                semanticModel, plannedIndexers, indexerPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)indexerRewriter.Visit(workingRoot)!;
        }
        if (workingRoot.IsEquivalentTo(root))
=======
        statistics.PropertiesPlanned += plannedProps.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOps.Count(p => IsFirstDeclarationFile(p, filePath));

        // 链式顺序（设计 §4.5 修订版）：Property 先、Operator 后。属性重写器的声明级
        // 绑定（GetDeclaredSymbol）只认原始树节点，只能跑在未改写的根上；属性重写会
        // 重建祖先节点并合成新内层（set_X(get_X() op v)），原始 SemanticModel 无法绑定
        // 重挂载节点。因此树已变时为 operator 重写器换用替换树的新鲜编译模型，
        // plan 经稳定键（类型::op 名(参数):返回）桥接（设计 §4.6）。
        var current = root;
        if (plannedProps.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProps, active.OfType<Properties.PropertyPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = (CompilationUnitSyntax)rewriter.Visit(current)!;
        }
        if (plannedOps.Count > 0 || hasOperatorDecls)
        {
            SemanticModel operatorModel;
            if (ReferenceEquals(current, root))
            {
                operatorModel = semanticModel;
            }
            else
            {
                // 属性产物含无分隔 trivia 的合成节点（文本级 publicint/returnp），必须先经
                // 格式化+本地校验取干净文本，再解析回编译换取可绑定的新鲜模型
                //（重写器访问的是替换树自身的节点实例；plan 走 §4.6 稳定键桥接）。
                var intermediate = FormatAndValidate(current);
                if (intermediate == null)
                {
                    statistics.FilesSkippedClean++;
                    diagnostics.Add(new MemberDesugarDiagnostic(
                        MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                        "member-desugar rewrite failed local parse validation; the original file was " +
                        "preserved pending whole-tree validation.", filePath, 0, 0, null));
                    return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
                }
                var (replacementModel, replacementRoot) = ParseReplacement(semanticModel, intermediate);
                operatorModel = replacementModel;
                current = replacementRoot;
            }
            var opRewriter = new Operators.OperatorUseSiteRewriter(
                operatorModel, plannedOps, active.OfType<Operators.OperatorPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = (CompilationUnitSyntax)opRewriter.Visit(current)!;
        }
        var newRoot = current;
        if (newRoot.IsEquivalentTo(root))
>>>>>>> c5fd759b'''

new = '''        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOperators.Count(p => IsFirstDeclarationFile(p, filePath));

        // 四级 dispatch：属性→事件(重解析)→运算符(重解析)→索引器
        // 属性改写器在原始树上运行；事件/运算符改写器经替换树运行（产物是红色节点，
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
        if (workingRoot.IsEquivalentTo(root))'''

content = content.replace(old, new)

with open('src/CSharpToJava.Core/MemberDesugar/MemberDesugarPreprocessor.cs', 'w') as f:
    f.write(content)
print('Done')
