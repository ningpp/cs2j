using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// TsRawStatementNormalizer 组件级测试（Task 11 引入）。
/// 前端对 Console.WriteLine/foreach 产出 JavaRawStatement 直通（非结构化节点），
/// 发射前经最小文本归一化得到合法 TS；不匹配的 raw 语句原样直通。
/// </summary>
public class TsRawStatementNormalizerTests
{
    [Fact]
    public void System_Out_Println_Maps_To_Console_Log()
    {
        Assert.Equal(
            "console.log(\"hello\");",
            TsRawStatementNormalizer.Normalize("System.out.println(\"hello\");"));
    }

    [Fact]
    public void System_Err_Println_Maps_To_Console_Error()
    {
        Assert.Equal(
            "console.error(\"oops\");",
            TsRawStatementNormalizer.Normalize("System.err.println(\"oops\");"));
    }

    [Fact]
    public void Println_With_Arguments_Preserves_Expression_And_Semicolon()
    {
        Assert.Equal(
            "console.log(\"total: \" + total);",
            TsRawStatementNormalizer.Normalize("System.out.println(\"total: \" + total);"));
    }

    [Fact]
    public void Println_Inside_If_Guard_Is_Normalized()
    {
        // WriteLineIf 前端产出 "if (cond) System.out.println(x);" 形式的 raw 语句
        Assert.Equal(
            "if (cond) console.log(x);",
            TsRawStatementNormalizer.Normalize("if (cond) System.out.println(x);"));
    }

    [Fact]
    public void Enhanced_For_Maps_To_For_Of_Preserving_Multiline_Body()
    {
        var input = "for (int item : arr) {\n        total = total + item;\n    }";
        var expected = "for (const item of arr) {\n        total = total + item;\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_With_Quoted_String_Argument_Is_Normalized()
    {
        var input = "for (String part : \"a,b,c\".split(\",\")) {\n        console.log(part);\n    }";
        var expected = "for (const part of \"a,b,c\".split(\",\")) {\n        console.log(part);\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_With_Method_Call_Collection_Is_Normalized()
    {
        var input = "for (char c : name.toCharArray()) { System.out.println(c); }";
        var expected = "for (const c of name.toCharArray()) { console.log(c); }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Enhanced_For_Without_Braces_Is_Normalized()
    {
        var input = "for (int item : arr) System.out.println(item);";
        var expected = "for (const item of arr) console.log(item);";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Generic_Enhanced_For_Type_Is_Dropped()
    {
        var input = "for (Map.Entry<K, V> entry : map.entrySet()) { process(entry); }";
        var expected = "for (const entry of map.entrySet()) { process(entry); }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Non_For_Statement_Passes_Through()
    {
        var input = "if (x > 0) { return 1; } else { return 0; }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Plain_Statement_Passes_Through()
    {
        var input = "int total = 0;";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Classic_For_Without_Colon_Passes_Through()
    {
        // 经典 for 头不含 " : "，绝不走增强 for 改写路径（1 参重载无映射表，直通；
        // let 化经 2 参重载由 Classic_For_With_Int_Declaration_Is_Let_ified 覆盖）。
        var input = "for (int i = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Classic_For_With_Ternary_In_Header_Passes_Through()
    {
        // 普通 for 头内三元含 " : "，但 " : " 左侧末 token 非标识符，必须直通避免误改写为
        // for-of（1 参重载无映射表；2 参重载下三元初始化器经 let 化，见
        // Classic_For_With_Ternary_Initializer_Is_Let_ified）。
        var input = "for (int i = cond ? 0 : 1; i < n; i++) { work(i); }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Empty_String_Passes_Through()
    {
        Assert.Equal(string.Empty, TsRawStatementNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Classic_For_With_Ternary_Identifier_Operands_Passes_Through()
    {
        // 回归（Task 11 复审 Bug 1）：三元操作数为标识符（flag ? a : b）时，旧守卫
        // "左侧末 token 为标识符" 无法拦截（a : 满足），会被误改写为非法 for-of；
        // 增强 for 头不含顶层 ";"，普通 for（含任意三元）不得走 for-of 改写
        //（1 参重载直通；2 参重载下三元初始化器经 let 化，见
        // Classic_For_With_Ternary_Initializer_Is_Let_ified）。
        var input = "for (int i = flag ? a : b; i < n; i++) { work(i); }";
        Assert.Equal(input, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Println_Argument_Containing_Println_Text_Is_Preserved()
    {
        // 回归（Task 11 复审 Bug 2）：实参字符串字面量内含 "System.out.println(" 文本时，
        // 旧整串 Replace 会连同字面量内容一并改写（console.log("console.log(x)")），
        // 破坏实参语义；替换必须跳过引号内区域。
        Assert.Equal(
            "console.log(\"System.out.println(x)\");",
            TsRawStatementNormalizer.Normalize("System.out.println(\"System.out.println(x)\");"));
    }

    [Fact]
    public void Nested_Enhanced_Fors_Are_All_Normalized()
    {
        // 回归（Task 11 复审 Bug 3）：嵌套 foreach 整段 raw 直通时，旧实现只归一化
        // 语句起始的最外层 for 头，内层 for (int item : row) 原样保留（非法 TS）；
        // 修复后同一文本内所有增强 for 头均改写，body 原样保留。
        var input = "for (int row : arr) {\n        for (int item : row) {\n            total = total + item;\n        }\n    }";
        var expected = "for (const row of arr) {\n        for (const item of row) {\n            total = total + item;\n        }\n    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    // ---- Task 13：经典 for 头声明段 let 化（2 参重载，映射表走 TsMemberMappingTable）----

    private static string NormalizeWithTable(string code)
    {
        var table = new TsMemberMappingTable(null, null);
        var writer = new TsExpressionWriter(table, new TsImportCollector());
        return TsRawStatementNormalizer.Normalize(code, writer.MapType);
    }

    [Fact]
    public void Classic_For_With_Int_Declaration_Is_Let_ified()
    {
        // Task 13 登记项（Task 11 注记⑥）：raw 经典 for 头保持 Java 形态
        // "for (int i = 0; ...)"（含非法 TS 声明 int i = 0），let 化后
        // "for (let i: number = 0; ...)"，类型经 TsMemberMappingTable（int → number）。
        var input = "for (int i = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        var expected = "for (let i: number = 0; i < n; i = i + 1) {\n        total = total + i;\n    }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_String_Declaration_Is_Let_ified()
    {
        // String → string（TsMemberMappingTable 内置 Java 风味类型表）
        var input = "for (String s = \"\"; s.length() > 0; s = s.substring(1)) { use(s); }";
        var expected = "for (let s: string = \"\"; s.length() > 0; s = s.substring(1)) { use(s); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Multi_Variable_Declaration_Is_Let_ified()
    {
        // M5 / MSAGL: multi-declarator init (int i = 0, j = 1) becomes a TS let
        // declarator list with the shared type on each declarator.
        var input = "for (int i = 0, j = 1; i < n; i++) { work(i, j); }";
        Assert.Equal("for (let i: number = 0, j: number = 1; i < n; i++) { work(i, j); }", NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_Without_Initializer_Is_Let_ified()
    {
        // M5 / MSAGL: no-initializer declaration (int i;) becomes let i: number;
        var input = "for (int i; i < n; i++) { work(i); }";
        Assert.Equal("for (let i: number; i < n; i++) { work(i); }", NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_Empty_Header_Passes_Through()
    {
        // for (;;) 声明段为空 → 直通（保留无限循环形态）
        var input = "for (;;) { work(); }";
        Assert.Equal(input, NormalizeWithTable(input));
    }
    // ---- Round 7 (MSAGL): 泛型声明 + lambda 初始化器 let 化 ----
    // 旧类型正则 `(?:<[^;\r\n]*>)?` 会把声明名与初始化器里 lambda `=>` 的 `>` 吞进
    // 类型组，导致 TryRewriteDeclarators 收到非声明文本而放弃 let 化（TS1005/TS1011）。
    // 平衡尖括号版本只匹配类型自身的闭合 `>`（含嵌套泛型），lambda 保留在初始化器内。

    [Fact]
    public void Generic_Declaration_With_Lambda_Initializer_Is_Let_ified()
    {
        // MSAGL LineSweeperForPortLocations.cs:533
        var input = "RBNode<ConeSide> rbNode = rightConeSides.findLast(s => pointIsToTheRightOfSegment(site, s));";
        var expected = "let rbNode: RBNode<ConeSide> = rightConeSides.findLast(s => pointIsToTheRightOfSegment(site, s));";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Nested_Generic_Fqcn_Declaration_With_Lambda_Is_Let_ified()
    {
        // MSAGL GreedyNodeRailLevelCalculator.cs:88；类型注解内 FQCN 实参走映射缩短，
        // 初始化器文本原样保留。Round 19（TS2503）：泛型实参位（after=='>'）同样缩短
        // ——"new Set<SymmetricTuple<Microsoft.Msagl.Core.Geometry.Point>>(...)" 的实参
        // FQCN 此前直通产生 Cannot find namespace 'Microsoft'，现按类型规则整链缩短。
        var input = "Set<SymmetricTuple<Microsoft.Msagl.Core.Geometry.Point>> segments = new Set<SymmetricTuple<Microsoft.Msagl.Core.Geometry.Point>>(Enumerable.select(edges, e => new SymmetricTuple<Microsoft.Msagl.Core.Geometry.Point>(e.getSourcePoint(), e.getTargetPoint())));";
        var expected = "let segments: Set<SymmetricTuple<Point>> = new Set<SymmetricTuple<Point>>(Enumerable.select(edges, e => new SymmetricTuple<Point>(e.getSourcePoint(), e.getTargetPoint())));";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Generic_Stream_Declaration_With_Lambda_Is_Let_ified()
    {
        // MSAGL LgInteractor.cs:1985
        var input = "Stream<LgNodeInfo> neighb = Enumerable.orderBy(this.getNeighborsOnLevel(s, i), n => n.getZoomLevel());";
        var expected = "let neighb: Stream<LgNodeInfo> = Enumerable.orderBy(this.getNeighborsOnLevel(s, i), n => n.getZoomLevel());";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Generic_Type_And_Lambda_Is_Let_ified()
    {
        // MSAGL LinkedPointSplitter.cs:67/73：经典 for 头声明段泛型类型 + lambda 初始化器
        var input = "for (RBNode<LinkedPoint> node = tree.findFirst(p => left <= p.getPoint().X); node !== null && node.Item.getPoint().X <= right; node = tree.next(node)) {";
        var expected = "for (let node: RBNode<LinkedPoint> = tree.findFirst(p => left <= p.getPoint().X); node !== null && node.Item.getPoint().X <= right; node = tree.next(node)) {";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Ternary_Initializer_Is_Let_ified()
    {
        // 与三元场景共存：声明段 let 化，三元初始化器原样保留（Task 11 复审 Bug 1
        // 的 for-of 误改写回归由 Classic_For_With_Ternary_Identifier_Operands_Passes_Through 守卫）
        var input = "for (int i = (flag != 0 ? a : b); i < 10; i = i + 1) { work(i); }";
        var expected = "for (let i: number = (flag != 0 ? a : b); i < 10; i = i + 1) { work(i); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    // ---- Task 13：raw 语句内 char 字面量 → 双引号字符串 ----

    [Fact]
    public void Char_Literal_In_Raw_Statement_Is_Double_Quoted()
    {
        // 前端 return 语句为 JavaRawStatement 直通（return 'A';），不经表达式发射层；
        // char 字面量归一化为双引号字符串，与 char → string 映射语义一致。
        Assert.Equal("return \"A\";", NormalizeWithTable("return 'A';"));
    }

    [Fact]
    public void Char_Literal_Newline_Escape_Is_Preserved()
    {
        // '\n' → "\n"（转义在双引号字符串中原样合法）
        Assert.Equal("return \"\\n\";", NormalizeWithTable("return '\\n';"));
    }

    [Fact]
    public void Char_Literal_Single_Quote_Escape_Is_Unescaped()
    {
        // '\'' → "'"（单引号转义在双引号字符串中不需要）
        Assert.Equal("return \"'\";", NormalizeWithTable("return '\\'';"));
    }

    [Fact]
    public void Char_Literal_Inside_String_Is_Not_Touched()
    {
        // 双引号字符串区域整体跳过，内部单引号（如 it's）不被改写
        var input = "return \"it's a test\";";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    [Fact]
    public void Char_Literal_With_Double_Quote_Passes_Through()
    {
        // 内含双引号的 '\"' 保持单引号直通（M2 字符处理任务细化，与表达式层边界一致）
        var input = "return '\\\"';";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    // ─── Task 22：raw 文本 long 字面量 L → n ──────────────────────────

    [Fact]
    public void Long_Literal_Suffix_Emits_Bigint()
    {
        // 数值语义 rewriter 改写出的混合链落在 raw 文本（7L + BigInt(1)），L 后缀必须归一化
        Assert.Equal("return 7n + BigInt(1);", NormalizeWithTable("return 7L + BigInt(1);"));
        Assert.Equal("return 100n;", NormalizeWithTable("return 100L;"));
    }

    [Fact]
    public void Long_Literal_Hex_And_Underscore_Emits_Bigint()
    {
        Assert.Equal("return 0xFFn;", NormalizeWithTable("return 0xFFL;"));
        Assert.Equal("return 1_000n;", NormalizeWithTable("return 1_000L;"));
    }

    [Fact]
    public void Long_Literal_Inside_String_And_Identifier_Not_Touched()
    {
        // 字符串内 L 后缀不改写；标识符中的数字段（abc123L）不做字面量改写
        Assert.Equal("return \"7L\";", NormalizeWithTable("return \"7L\";"));
        Assert.Equal("return abc123L;", NormalizeWithTable("return abc123L;"));
    }

    // ---- M5 / MSAGL: raw method-body Java text normalization ----

    [Fact]
    public void Plain_Declaration_Without_Initializer_Is_Let_ified()
    {
        Assert.Equal("let x: number;", NormalizeWithTable("double x;"));
        Assert.Equal("let par: number;", NormalizeWithTable("double par;"));
    }

    [Fact]
    public void Multi_Variable_Declaration_Is_Let_ified()
    {
        Assert.Equal("let aSol: number, bSol: number;", NormalizeWithTable("double aSol, bSol;"));
        Assert.Equal("let s: number, e: number;", NormalizeWithTable("double s, e;"));
        Assert.Equal("let aIsInsideB: boolean, bIsInsideA: boolean;", NormalizeWithTable("boolean aIsInsideB, bIsInsideA;"));
    }

    [Fact]
    public void Multi_Variable_With_Initializers_Is_Let_ified()
    {
        // Mixed declarators: TS supports let a: T = i, b: T; per declarator.
        Assert.Equal("let x: number = 1, y: number;", NormalizeWithTable("double x = 1, y;"));
    }

    [Fact]
    public void Array_Type_Declaration_Is_Let_ified()
    {
        Assert.Equal("let layer: number[] = nla.getLayers()[i];", NormalizeWithTable("int[] layer = nla.getLayers()[i];"));
        Assert.Equal("let words: string[] = CsString.split(line, \",\");", NormalizeWithTable("String[] words = CsString.split(line, \",\");"));
    }

    [Fact]
    public void Array_Type_With_Whitelisted_Identity_Element_Is_Let_ified()
    {
        // A2 (Round 12): boolean[] maps identically (boolean -> boolean element),
        // so the identity whitelist check saw "boolean[]" as unknown; the base
        // decides, the [] suffix is preserved.
        Assert.Equal("let enqueueed: boolean[] = new Array(graph.getNodeCount());",
            NormalizeWithTable("boolean[] enqueueed = new boolean[graph.getNodeCount()];"));
        Assert.Equal("let seen: boolean[][] = new Array(n);",
            NormalizeWithTable("boolean[][] seen = new boolean[n][];"));
    }

    [Fact]
    public void Fqcn_Type_Declaration_Is_Let_ified()
    {
        // Project FQCN maps to the last segment via TsMemberMappingTable.
        Assert.Equal("let x: Point;", NormalizeWithTable("Microsoft.Msagl.Core.Geometry.Point x;"));
        Assert.Equal("let a: Point, b: Point;", NormalizeWithTable("Microsoft.Msagl.Core.Geometry.Point a, b;"));
        Assert.Equal("let p: Point = new Point(1, 2);", NormalizeWithTable("Microsoft.Msagl.Core.Geometry.Point p = new Microsoft.Msagl.Core.Geometry.Point(1, 2);"));
    }

    [Fact]
    public void Fqcn_Static_Method_Call_Is_Shortened()
    {
        // Round 18（MSAGL TS2304 'Microsoft' 族）：static 调用
        // Microsoft.Msagl.Core.Geometry.Point.op_Multiply(ct, ct) 此前在
        // after=='(' 且非 "new " 形态下直通——链末段是方法名，须按成员调用规则
        // 缩短类型前缀（此前只缩短 after=='.' 形态）。
        Assert.Equal(
            "secondDerivative = Point.op_Multiply(ct, ct) + Point.op_Multiply((Point.op_Subtract(c, a)), ctt);",
            NormalizeWithTable("secondDerivative = Microsoft.Msagl.Core.Geometry.Point.op_Multiply(ct, ct) + Microsoft.Msagl.Core.Geometry.Point.op_Multiply((Microsoft.Msagl.Core.Geometry.Point.op_Subtract(c, a)), ctt);"));
        Assert.Equal(
            "dt = Point.op_Multiply((Point.op_Subtract(c, a)), ct) / secondDerivative;",
            NormalizeWithTable("dt = Microsoft.Msagl.Core.Geometry.Point.op_Multiply((Microsoft.Msagl.Core.Geometry.Point.op_Subtract(c, a)), ct) / secondDerivative;"));
        Assert.Equal(
            "return (Point.subtract(curve.get_Item(hint), a)).getLength();",
            NormalizeWithTable("return (Microsoft.Msagl.Core.Geometry.Point.subtract(curve.get_Item(hint), a)).getLength();"));
    }

    [Fact]
    public void Fqcn_Static_Method_Call_Guards()
    {
        // 无点分链（Java 全局名）不受影响；java./io.github./System./单段前缀
        // 经 mapType 恒等返回，不得误缩短。
        Assert.Equal(
            "Stream.concat(a, b).map(n => n.getBoundingBox());",
            NormalizeWithTable("Stream.concat(a, b).map(n -> n.getBoundingBox());"));
        Assert.Equal(
            "System.arraycopy(this.A, 1, newA, 1, this.heapSize);",
            NormalizeWithTable("System.arraycopy(this.A, 1, newA, 1, this.heapSize);"));
        Assert.Equal(
            "x = io.github.ningpp.compat.StringHelper.parseInt(s);",
            NormalizeWithTable("x = io.github.ningpp.compat.StringHelper.parseInt(s);"));
        Assert.Equal(
            "throw new java.util.NoSuchElementException();",
            NormalizeWithTable("throw new java.util.NoSuchElementException();"));
        // 既有 new FQCN(...) 形态回归守卫
        Assert.Equal(
            "return new Point(1, 2);",
            NormalizeWithTable("return new Microsoft.Msagl.Core.Geometry.Point(1, 2);"));
        // 短类型名 + 静态成员的 3 段链：前缀（Timer.GlobalTick）不是类型名，
        // 不得缩短（否则剥掉 Timer. 限定产生裸静态成员）
        Assert.Equal(
            "Timer.GlobalTick.add((seconds) => this.onTick(seconds));",
            NormalizeWithTable("Timer.GlobalTick.add((seconds) => this.onTick(seconds));"));
    }

    [Fact]
    public void Class_Like_Unmapped_Type_Declaration_Is_Let_ified()
    {
        // Single-segment user types (Rail) stay unmapped but must still be let-ified.
        Assert.Equal("let rail: Rail;", NormalizeWithTable("Rail rail;"));
        Assert.Equal("let ret: Set<T> = Set.Create$2(set0);", NormalizeWithTable("Set<T> ret = Set.Create$2(set0);"));
    }

    [Fact]
    public void Classic_For_With_Class_Like_Unmapped_Type_Is_Let_ified()
    {
        var input = "for (PolylinePoint pp = poly.getStartPoint(); pp !== null; pp = pp.getNext()) { use(pp); }";
        var expected = "for (let pp: PolylinePoint = poly.getStartPoint(); pp !== null; pp = pp.getNext()) { use(pp); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Classic_For_With_Generic_Type_Is_Let_ified()
    {
        var input = "for (RectangleNode<T, P> node = parent.getParent(); node !== null; node = node.getParent()) { use(node); }";
        var expected = "for (let node: RectangleNode<T, P> = parent.getParent(); node !== null; node = node.getParent()) { use(node); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Enhanced_For_With_Iterable_Cast_Wrappers_Strips_Casts()
    {
        // Java erasure casts around the collection expression are dropped.
        var input = "for (const e of (Iterable<Edge>)(Iterable<?>)(graph.getEdges())) { use(e); }";
        var expected = "for (const e of (graph.getEdges())) { use(e); }";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void If_With_Identifier_Condition_Keeps_Condition()
    {
        // Regression: "(X) return" was misread as a reference cast and the
        // parenthesized condition was dropped, yielding "if return true;"
        // (iterator bridge hasNext bodies).
        Assert.Equal(
            "if (_iteratorHasNext) return true;",
            NormalizeWithTable("if (_iteratorHasNext) return true;"));
    }

    [Fact]
    public void Control_Flow_Conditions_Are_Not_Treated_As_Casts()
    {
        Assert.Equal("if (Value) return true;", NormalizeWithTable("if (Value) return true;"));
        Assert.Equal("while (Ready) work();", NormalizeWithTable("while (Ready) work();"));
        Assert.Equal("if (X) throw new RuntimeException();", NormalizeWithTable("if (X) throw new RuntimeException();"));
    }

    [Fact]
    public void Reference_Casts_In_Expressions_Are_Still_Stripped()
    {
        Assert.Equal("return n;", NormalizeWithTable("return (Node) n;"));
        Assert.Equal("x = es;", NormalizeWithTable("x = (Iterable<Edge>) es;"));
        Assert.Equal("f(p);", NormalizeWithTable("f((Point) p);"));
    }

    [Fact]
    public void Float_Literal_Suffix_Is_Dropped_In_Raw_Text()
    {
        Assert.Equal("return 2.0;", NormalizeWithTable("return 2.0f;"));
        Assert.Equal("let a: number = 1.5;", NormalizeWithTable("double a = 1.5f;"));
    }



    [Fact]
    public void Method_Reference_This_Pack_Becomes_Arrow_Function()
    {
        Assert.Equal(
            "goldenSectionSearch((...args) => this.pack(...args), lowerBound, c0, upperBound, precision);",
            TsRawStatementNormalizer.Normalize("goldenSectionSearch(this::pack, lowerBound, c0, upperBound, precision);"));
    }

    [Fact]
    public void Method_Reference_Receiver_Becomes_Arrow_Function()
    {
        Assert.Equal(
            "B b = (...args) => trimSeg.b(...args);",
            TsRawStatementNormalizer.Normalize("B b = trimSeg::b;"));
    }

    [Fact]
    public void Method_Reference_Boxed_Primitive_Becomes_Number_Conversion()
    {
        Assert.Equal(
            "return order.stream().mapToInt((x) => Number(x)).toArray();",
            TsRawStatementNormalizer.Normalize("return order.stream().mapToInt(Integer::intValue).toArray();"));
        Assert.Equal(
            "double v = d.doubleValue();",
            TsRawStatementNormalizer.Normalize("double v = d.doubleValue();"));
    }

    [Fact]
    public void Array_Constructor_Reference_Becomes_Array_Creation()
    {
        Assert.Equal(
            "this.matrixQ[0] = this.newMatrixRow.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("this.matrixQ[0] = this.newMatrixRow.toArray(MatrixCell[]::new);"));
    }

    [Fact]
    public void Array_Constructor_Reference_With_Fully_Qualified_Name_Drops_Entire_Type()
    {
        // Round 10 B: back-scan must walk over "." segments so the whole FQCN
        // receiver is dropped, not just the last segment.
        Assert.Equal(
            "points = list.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("points = list.toArray(Microsoft.Msagl.Core.Geometry.Point[]::new);"));
        Assert.Equal(
            "points = list.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("points = list.toArray(Core.Geometry.Point[]::new);"));
    }

    [Fact]
    public void Array_Constructor_Reference_With_Simple_Type_Still_Works()
    {
        Assert.Equal(
            "cells = row.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("cells = row.toArray(Point[]::new);"));
        Assert.Equal(
            "arr = stream.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("arr = stream.toArray(Integer[]::new);"));
    }

    [Fact]
    public void Non_Array_Method_References_Unaffected_By_Array_Constructor_Fix()
    {
        Assert.Equal(
            "actions.forEach((x) => x.run());",
            TsRawStatementNormalizer.Normalize("actions.forEach(Runnable::run);"));
        Assert.Equal(
            "goldenSectionSearch((...args) => this.pack(...args), lowerBound);",
            TsRawStatementNormalizer.Normalize("goldenSectionSearch(this::pack, lowerBound);"));
    }

    [Fact]
    public void Java_Lambda_Arrow_Becomes_TypeScript_Arrow()
    {
        Assert.Equal(
            "return Stream.concat(a, b).map(n => n.getBoundingBox());",
            TsRawStatementNormalizer.Normalize("return Stream.concat(a, b).map(n -> n.getBoundingBox());"));
        Assert.Equal(
            "xs.map((a, b) => a + b);",
            TsRawStatementNormalizer.Normalize("xs.map((a, b) -> a + b);"));
    }

    [Fact]
    public void Method_Reference_And_Lambda_Inside_String_Literals_Are_Preserved()
    {
        Assert.Equal(
            "console.log(\"a::b -> c\");",
            TsRawStatementNormalizer.Normalize("System.out.println(\"a::b -> c\");"));
    }


    [Fact]
    public void Comment_With_Apostrophe_Does_Not_Swallow_Following_For_Head()
    {
        var input = "// we've made the matrix such that Q[i][i] is 1\nfor (int rowNum = 0; rowNum < this.matrixQ.length; ++rowNum) {";
        var expected = "// we've made the matrix such that Q[i][i] is 1\nfor (let rowNum: number = 0; rowNum < this.matrixQ.length; ++rowNum) {";
        Assert.Equal(expected, NormalizeWithTypeMap(input));
    }

    [Fact]
    public void For_Inside_Comment_Is_Not_Rewritten()
    {
        var input = "//   for all jj\nfor (QpscVar qvar : this.vectorQpscVars) {";
        var expected = "//   for all jj\nfor (const qvar of this.vectorQpscVars) {";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Declaration_With_Trailing_Comment_Is_Let_ified()
    {
        var input = "double HalfLife = 50.0; // 50 was chosen as the default";
        var expected = "let HalfLife: number = 50.0; // 50 was chosen as the default";
        Assert.Equal(expected, NormalizeWithTypeMap(input));
    }

    [Fact]
    public void Declaration_With_Array_Type_And_Trailing_Comment_Is_Let_ified()
    {
        var input = "VertStatus[] status = new Array(graph.getNodeCount()); //will be Unvisited at the beginning";
        var expected = "let status: VertStatus[] = new Array(graph.getNodeCount()); //will be Unvisited at the beginning";
        Assert.Equal(expected, NormalizeWithTypeMap(input));
    }

    private static string NormalizeWithTypeMap(string code)
        => TsRawStatementNormalizer.Normalize(code, t => t switch
        {
            "double" or "int" or "long" or "float" or "short" or "byte" => "number",
            _ => t,
        });


    [Fact]
    public void Catch_With_Type_Annotation_Keeps_Only_The_Binding_Name()
    {
        Assert.Equal(
            "} catch (__e) {",
            TsRawStatementNormalizer.Normalize("} catch (Exception __e) {"));
        Assert.Equal(
            "} catch (ex) {",
            TsRawStatementNormalizer.Normalize("} catch (CsException ex) {"));
    }

    [Fact]
    public void Try_With_Resources_Without_Catch_Or_Finally_Becomes_Block_Scope()
    {
        // C# using with no catch/finally: a bare "try {" is invalid TS (TS1472);
        // the resource declaration drops to a plain block that keeps its scope.
        var input =
            "try (StreamWrapper stream = StreamWrapper.of(FileHelper.openRead(fileName))) {\n" +
            "        var graphReader = new GeometryGraphReader(stream);\n" +
            "        return graph;\n" +
            "    }";
        var expected =
            "{ let stream = StreamWrapper.of(FileHelper.openRead(fileName));\n" +
            "        var graphReader = new GeometryGraphReader(stream);\n" +
            "        return graph;\n" +
            "    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Try_With_Resources_With_Catch_Keeps_Try_Structure()
    {
        var input =
            "try (StreamWriter writer = new StreamWriter(path)) {\n" +
            "        writer.write(\"x\");\n" +
            "    } catch (IOException e) {\n" +
            "        return;\n" +
            "    }";
        var expected =
            "try { let writer = new StreamWriter(path);\n" +
            "        writer.write(\"x\");\n" +
            "    } catch (e) {\n" +
            "        return;\n" +
            "    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Try_With_Resources_With_Finally_Keeps_Try_Structure()
    {
        var input =
            "try (StreamWriter writer = new StreamWriter(path)) {\n" +
            "        writer.write(\"x\");\n" +
            "    } finally {\n" +
            "        writer.close();\n" +
            "    }";
        var expected =
            "try { let writer = new StreamWriter(path);\n" +
            "        writer.write(\"x\");\n" +
            "    } finally {\n" +
            "        writer.close();\n" +
            "    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Try_With_Resources_With_Nested_Body_Blocks_Becomes_Block_Scope()
    {
        var input =
            "try (var file = new StreamReader(path)) {\n" +
            "        if (line == null) {\n" +
            "            break;\n" +
            "        }\n" +
            "    }";
        var expected =
            "{ let file = new StreamReader(path);\n" +
            "        if (line == null) {\n" +
            "            break;\n" +
            "        }\n" +
            "    }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input));
    }

    [Fact]
    public void Try_With_Resources_Head_Fragment_Without_Body_Keeps_Try_Prefix()
    {
        // Head-only fragments (no closing brace in the same text) keep the
        // pre-existing fallback; real emission always includes the full body.
        Assert.Equal(
            "try { let stream = StreamWrapper.of(FileHelper.openRead(fileName));",
            TsRawStatementNormalizer.Normalize("try (StreamWrapper stream = StreamWrapper.of(FileHelper.openRead(fileName))) {"));
    }

    [Fact]
    public void Synchronized_Block_Keeps_Its_Body()
    {
        Assert.Equal(
            "{ if (this.labelObstacleMap === null) {",
            TsRawStatementNormalizer.Normalize("synchronized (this) { if (this.labelObstacleMap === null) {"));
    }

    [Fact]
    public void Double_Literal_Suffix_Drops()
    {
        Assert.Equal(
            "self.OrigWeight = 0.0;",
            TsRawStatementNormalizer.Normalize("self.OrigWeight = 0.0d;"));
        Assert.Equal(
            "self.OrigScale = 0.0;",
            TsRawStatementNormalizer.Normalize("self.OrigScale = 0.0D;"));
    }

    [Fact]
    public void Java_Array_Dimension_Creation_Becomes_Array_Constructor()
    {
        Assert.Equal(
            "this.matrixQ = new Array(cVariables);",
            TsRawStatementNormalizer.Normalize("this.matrixQ = new MatrixCell[cVariables][];"));
        Assert.Equal(
            "private elements: number[][] = new Array(2);",
            TsRawStatementNormalizer.Normalize("private elements: number[][] = new double[2][];"));
        Assert.Equal(
            "new Array(this.NumRow)",
            TsRawStatementNormalizer.Normalize("new double[this.NumRow]"));
    }

    [Fact]
    public void Generic_Method_Invocation_Drops_Explicit_Type_Arguments()
    {
        Assert.Equal(
            "Collections.emptyList()",
            TsRawStatementNormalizer.Normalize("Collections.<EdgeGeometry>emptyList()"));
    }
    // ---- Round 11 (MSAGL): CRLF raw text with trailing comment let-ification ----
    // Constructor bodies flattened via AppendLine() (\r\n) carry a trailing \r on
    // every raw line; the old tail regex `\s*(?://[^\r\n]*)?` stops before that \r
    // after a trailing comment, so $ cannot match and the line is left un-rewritten
    // (Tiling.cs:70 / ProcrustesCircleConstraint.cs:28 ctor-factory paths, TS1434).

    [Fact]
    public void Crlf_Declaration_With_Trailing_Comment_Is_Let_ified()
    {
        var input = "        double r = 10 * (self.n as number); // we make our circle a reasonable size to aid numerical precision\r\n";
        var expected = "        let r: number = 10 * (self.n as number); // we make our circle a reasonable size to aid numerical precision\r\n";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Crlf_Multi_Variable_Declaration_With_Trailing_Comment_Is_Let_ified()
    {
        var input = "        int m, n, k = 1; //node location and inddex\r\n";
        var expected = "        let m: number, n: number, k: number = 1; //node location and inddex\r\n";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Crlf_Constructor_Body_Mirroring_Tiling_Is_Let_ified()
    {
        // Real pipeline shape: the flattened ctor body ends each line with \r\r\n
        // (raw line keeps \r, re-join adds \r\n; in-memory line end measured 13,13).
        var input =
            "const self = new Tiling();\r\r\n" +
            "        int m, n, k = 1; //node location and inddex\r\r\n" +
            "        self.N = bound;\r\r\n";
        var expected =
            "const self = new Tiling();\r\r\n" +
            "        let m: number, n: number, k: number = 1; //node location and inddex\r\r\n" +
            "        self.N = bound;\r\r\n";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Crlf_Declaration_Without_Comment_Is_Let_ified()
    {
        var input = "        int index = this.Numofnodes;\r\n";
        var expected = "        let index: number = this.Numofnodes;\r\n";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Lf_Declaration_With_Trailing_Comment_Is_Let_ified()
    {
        var input = "double HalfLife = 50.0; // 50 was chosen as the default\n";
        var expected = "let HalfLife: number = 50.0; // 50 was chosen as the default\n";
        Assert.Equal(expected, NormalizeWithTypeMap(input));
    }

    [Fact]
    public void Crlf_Declaration_With_String_Containing_Carriage_Return_Is_Let_ified()
    {
        // A \r inside a string literal must not be touched by the line-end fix.
        var input = "String s = \"a\rb\"; // keep\r\n";
        var expected = "let s: string = \"a\rb\"; // keep\r\n";
        Assert.Equal(expected, NormalizeWithTable(input));
    }
    // ---- Round 13 (MSAGL): multi-declarator array literals keep a second "new TYPE[]" prefix ----
    // ProcrustesCircleConstraint.cs:212-213: `double[,] XX = { {...} }, YY = { {...} };` becomes
    // raw Java text "double[][] XX = new double[][] { ... }, YY = new double[][] { ... };". The
    // array-literal pass matches its body greedily to the LAST '}' on the line, spanning both
    // declarators, so only the first "new double[][]" prefix is dropped; the second survives as
    // "YY = new double[][] [ ... ]" (TS1011 x2). Empty-bracket creations directly before a '['
    // literal lose the prefix too; non-empty dims (new double[2][]) keep the Array(n) path.

    [Fact]
    public void Multi_Declarator_Array_Literals_Strip_Every_New_Type_Prefix()
    {
        var input = "double[][] XX = new double[][] { { 1, 2 }, { -1, 2 } }, YY = new double[][] { { 0.07, 2.62 }, { 0.93, 3.12 } };";
        var expected = "let XX: number[][] = [ [ 1, 2 ], [ -1, 2 ] ], YY: number[][] = [ [ 0.07, 2.62 ], [ 0.93, 3.12 ] ];";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Empty_Dimension_Array_Creation_Before_Bracket_Literal_Drops_Prefix()
    {
        Assert.Equal("[ 1, 2 ]", TsRawStatementNormalizer.Normalize("new double[] [ 1, 2 ]"));
        Assert.Equal("[ [ 0.07 ], [ 0.93 ] ]", TsRawStatementNormalizer.Normalize("new double[][] [ [ 0.07 ], [ 0.93 ] ]"));
    }

    [Fact]
    public void Non_Empty_Dimension_Array_Creation_Is_Untouched_By_Prefix_Strip()
    {
        // Guard: "new double[2][]" must keep the Array(n) dimension path — the strip
        // requires at least one empty [] pair, so "new double" + "[2]" cannot match.
        Assert.Equal("new Array(2)", TsRawStatementNormalizer.Normalize("new double[2][]"));
    }

    // ---- Round 14 (MSAGL): out-param lambda-capture holder raw text ----
    // Relayout.cs:144-161 `HashSet<Node> addedNodes; ... TryGetValue(cluster, out addedNodes)`
    // with the variable captured by a lambda emits BuildLambdaCaptureHolderDeclaration raw text:
    //   @SuppressWarnings("unchecked") CSharpHashSet<Node>[] _addedNodes = (CSharpHashSet<Node>[]) new Object[] { null };
    // Sub-defects: (a) declPattern rejects a leading @ annotation, (b) NormalizeReferenceCasts
    // lookahead does not accept '[' so the cast survives the array-literal pass, (c) the
    // declaration must then let-ify (uppercase type + array suffix is accepted).

    [Fact]
    public void SuppressWarnings_Generic_Array_Holder_Declaration_Is_Let_ified()
    {
        var input = "@SuppressWarnings(\"unchecked\") CSharpHashSet<Node>[] _addedNodes = (CSharpHashSet<Node>[]) new Object[] { null };";
        var expected = "let _addedNodes: CSharpHashSet<Node>[] = [ null ];";
        Assert.Equal(expected, NormalizeWithTable(input));
    }

    [Fact]
    public void Reference_Cast_Before_Bracket_Array_Literal_Is_Stripped()
    {
        Assert.Equal("[ null ]", TsRawStatementNormalizer.Normalize("(CSharpHashSet<Node>[]) [ null ]"));
        Assert.Equal("x = [ 1, 2 ];", NormalizeWithTable("x = (Point[]) [ 1, 2 ];"));
    }

    [Fact]
    public void SuppressWarnings_On_Non_Declaration_Text_Is_Preserved()
    {
        var input = "@SuppressWarnings(\"unchecked\") class Holder {";
        Assert.Equal(input, NormalizeWithTable(input));
    }

    // ---- Round 15 (MSAGL, E 族): LINQ 重写产物方法引用归一化 ----
    // LayoutAlgorithmHelpers.cs:106 `double l = u.Union(v).Count() - u.Intersect(v).Count();`
    // 的 LINQ 重写产物：`StreamSupport.stream(u.spliterator(), false).filter(new HashSet<>(v)::contains)`。
    // 三个子缺陷同根因：Java 方法引用形态未做 TS 归一化 —— (a) 空菱形 `new X<>(` 未剥
    // （NormalizeGenericMethodInvocations 只认 `.<T>method(`），(b) `TryRewriteMethodReference`
    // 只认标识符/this/Type[] 接收者，表达式接收者 `new HashSet(v)::contains` 直通，
    // (c) 裸 `HashSet` 无全局桩（global.d.ts 只有 CSharpHashSet）。

    [Fact]
    public void Diamond_Constructor_Type_Arguments_Are_Stripped()
    {
        // Java 空菱形 new HashSet<>(v) 无 TS 对应（TS1005/TS1099）；仅剥空 <>，
        // 带实参的 new HashSet<Node>(v) 不误伤；点分 FQCN 构造同样剥。
        Assert.Equal("new HashSet(v);", TsRawStatementNormalizer.Normalize("new HashSet<>(v);"));
        Assert.Equal("new CSharpList(val);", TsRawStatementNormalizer.Normalize("new CSharpList<>(val);"));
        Assert.Equal("new io.github.ningpp.compat.ObjectHolder();",
            TsRawStatementNormalizer.Normalize("new io.github.ningpp.compat.ObjectHolder<>();"));
        Assert.Equal("new HashSet<Node>(v);", TsRawStatementNormalizer.Normalize("new HashSet<Node>(v);"));
    }

    [Fact]
    public void Method_Reference_On_New_Expression_Receiver_Becomes_Arrow_Function()
    {
        // Intersect 重写产物全链：菱形先剥、表达式接收者 :: 改写为箭头函数、裸 HashSet
        // 映射为 CSharpHashSet（raw 文本局部映射，不动 global.d.ts 桩）。
        Assert.Equal(
            "xs.filter((x) => new CSharpHashSet(v).contains(x));",
            TsRawStatementNormalizer.Normalize("xs.filter(new HashSet<>(v)::contains);"));
    }

    [Fact]
    public void Method_Reference_On_Parenthesized_Expression_Receiver_Becomes_Arrow_Function()
    {
        // 一般括号表达式接收者 (expr)::method -> (x) => (expr).method(x)。
        Assert.Equal(
            "xs.map((x) => (getSet()).add(x));",
            TsRawStatementNormalizer.Normalize("xs.map((getSet())::add);"));
    }

    [Fact]
    public void Method_Reference_On_Call_Expression_Receiver_Becomes_Arrow_Function()
    {
        // 方法调用接收者 name(args)::method -> (x) => name(args).method(x)。
        Assert.Equal(
            "xs.map((x) => createSet(v).contains(x));",
            TsRawStatementNormalizer.Normalize("xs.map(createSet(v)::contains);"));
    }

    [Fact]
    public void Method_Reference_On_Nested_Call_Receiver_Keeps_Outermost_Call()
    {
        // 接收者是嵌套调用的最外层完整调用 makeSet(seed(v))，不是内层 seed(v)。
        Assert.Equal(
            "xs.map((x) => makeSet(seed(v)).contains(x));",
            TsRawStatementNormalizer.Normalize("xs.map(makeSet(seed(v))::contains);"));
    }

    [Fact]
    public void Method_Reference_On_Member_Chain_Call_Receiver_Keeps_Whole_Chain()
    {
        // 成员链调用接收者 foo().bar(x)::method -> (x) => foo().bar(x).method(x)。
        Assert.Equal(
            "xs.map((x) => getSet().create(v).contains(x));",
            TsRawStatementNormalizer.Normalize("xs.map(getSet().create(v)::contains);"));
    }

    [Fact]
    public void Method_Reference_On_New_With_Real_Type_Arguments_Keeps_Type_Arguments()
    {
        // 带实参的构造接收者不误伤（菱形 pass 不动它；映射只换基名）。
        Assert.Equal(
            "xs.filter((x) => new CSharpHashSet<Node>(v).contains(x));",
            TsRawStatementNormalizer.Normalize("xs.filter(new HashSet<Node>(v)::contains);"));
    }

    [Fact]
    public void Identifier_And_Array_Receiver_Method_References_Still_Work()
    {
        // 既有接收者形态（标识符/this/Type[]::new）行为不变。
        Assert.Equal(
            "actions.forEach((x) => x.run());",
            TsRawStatementNormalizer.Normalize("actions.forEach(Runnable::run);"));
        Assert.Equal(
            "this.matrixQ[0] = this.newMatrixRow.toArray((n) => new Array(n));",
            TsRawStatementNormalizer.Normalize("this.matrixQ[0] = this.newMatrixRow.toArray(MatrixCell[]::new);"));
        Assert.Equal(
            "goldenSectionSearch((...args) => this.pack(...args), lowerBound);",
            TsRawStatementNormalizer.Normalize("goldenSectionSearch(this::pack, lowerBound);"));
    }


    // ================= Round 16: TS2663/TS2662 bare member references =================

    private static TsMemberContext MemberCtx(string className, string[] instance, string[] statics, string[]? typeParams = null, string[]? typeNames = null, string[]? selfTyped = null)
    {
        return new TsMemberContext
        {
            ClassName = className,
            InstanceMembers = instance.Length == 0 ? null : new HashSet<string>(instance),
            StaticMembers = statics.Length == 0 ? null : new HashSet<string>(statics),
            TypeParameters = typeParams is { Length: > 0 } ? new HashSet<string>(typeParams) : null,
            TypeNames = typeNames is { Length: > 0 } ? new HashSet<string>(typeNames) : null,
            SelfTypedMembers = selfTyped is { Length: > 0 } ? new HashSet<string>(selfTyped) : null,
        };
    }

    [Fact]
    public void Bare_Instance_Members_Get_This_Prefix()
    {
        var ctx = MemberCtx("BinaryHeapWithComparer", new[] { "A", "tClass" }, Array.Empty<string>());
        Assert.Equal(
            "var newA = TypeHelper.newArrayInstance(this.tClass, this.A.length * 2);",
            TsRawStatementNormalizer.Normalize("var newA = TypeHelper.newArrayInstance(tClass, A.length * 2);", memberContext: ctx));
    }

    [Fact]
    public void Bare_Static_Members_Get_ClassName_Prefix()
    {
        var ctx = MemberCtx("AlgorithmBase", Array.Empty<string>(), new[] { "threadStaticCancelToken", "LocalProgressStepSizeDefault" });
        Assert.Equal(
            "this.cancelToken = AlgorithmBase.threadStaticCancelToken;",
            TsRawStatementNormalizer.Normalize("this.cancelToken = threadStaticCancelToken;", memberContext: ctx));
    }

    [Fact]
    public void Bare_Instance_And_Static_Method_Calls_Get_Prefixes()
    {
        var ctx = MemberCtx("AlgorithmBase", new[] { "startListenToLocalProgress" }, new[] { "Reset" });
        Assert.Equal(
            "this.startListenToLocalProgress(expectedSteps, 1);",
            TsRawStatementNormalizer.Normalize("startListenToLocalProgress(expectedSteps, 1);", memberContext: ctx));
        Assert.Equal(
            "AlgorithmBase.Reset(x);",
            TsRawStatementNormalizer.Normalize("Reset(x);", memberContext: ctx));
    }

    [Fact]
    public void No_Context_Returns_Input_Unchanged()
    {
        Assert.Equal(
            "var newA = TypeHelper.newArrayInstance(tClass, A.length * 2);",
            TsRawStatementNormalizer.Normalize("var newA = TypeHelper.newArrayInstance(tClass, A.length * 2);"));
        Assert.Equal(
            "threadStaticCancelToken = old;",
            TsRawStatementNormalizer.Normalize("threadStaticCancelToken = old;"));
    }

    [Fact]
    public void Empty_Member_Sets_Return_Input_Unchanged()
    {
        var ctx = new TsMemberContext { ClassName = "Sample" };
        Assert.Equal("A = A + 1;", TsRawStatementNormalizer.Normalize("A = A + 1;", memberContext: ctx));
    }

    [Fact]
    public void Method_Parameter_Shadows_Field_And_Static_Member_Qualifies()
    {
        var ctx = MemberCtx("BinaryHeapWithComparer", new[] { "A", "tClass" }, new[] { "initialHeapCapacity" });
        var locals = new HashSet<string> { "tClass" };
        Assert.Equal(
            "this.A = TypeHelper.newArrayInstance(tClass, BinaryHeapWithComparer.initialHeapCapacity + 1);",
            TsRawStatementNormalizer.Normalize(
                "this.A = TypeHelper.newArrayInstance(tClass, initialHeapCapacity + 1);",
                memberContext: ctx, extraLocals: locals));
    }

    [Fact]
    public void Raw_Declarations_Shadow_Members()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "i", "parent", "son" }, Array.Empty<string>());
        var input = "let i: number = A.length;\nT parent, son;\nson = A[i];\nparent = i;";
        var expected = "let i: number = this.A.length;\nT parent, son;\nson = this.A[i];\nparent = i;";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input, memberContext: ctx));
    }

    [Fact]
    public void Multi_Declarator_Let_Shadow_Spans_Line_Breaks()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "a", "b" }, Array.Empty<string>());
        var input = "let a, b;\na = A;";
        var expected = "let a, b;\na = this.A;";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input, memberContext: ctx));
    }

    [Fact]
    public void For_Head_Locals_Shadow_Members()
    {
        var ctx = MemberCtx("Sample", new[] { "i", "n" }, Array.Empty<string>());
        var input = "for (let i = 0; i < n; i = i + 1) { use(i); }";
        var expected = "for (let i = 0; i < this.n; i = i + 1) { use(i); }";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input, memberContext: ctx));
        var ctx2 = MemberCtx("Sample", new[] { "x", "items" }, Array.Empty<string>());
        Assert.Equal(
            "for (const x of this.items) { use(x); }",
            TsRawStatementNormalizer.Normalize("for (const x of items) { use(x); }", memberContext: ctx2));
    }

    [Fact]
    public void Lambda_Params_Shadow_Members()
    {
        var ctx = MemberCtx("Sample", new[] { "a", "b", "x" }, Array.Empty<string>());
        Assert.Equal(
            "(a, b) => a + b",
            TsRawStatementNormalizer.Normalize("(a, b) -> a + b", memberContext: ctx));
        Assert.Equal(
            "x => x + 1",
            TsRawStatementNormalizer.Normalize("x -> x + 1", memberContext: ctx));
    }

    [Fact]
    public void Catch_Parameter_Shadows_Member()
    {
        var ctx = MemberCtx("Sample", new[] { "e", "A" }, Array.Empty<string>());
        Assert.Equal(
            "catch (e) { this.A = e; }",
            TsRawStatementNormalizer.Normalize("catch (e) { A = e; }", memberContext: ctx));
        Assert.Equal(
            "catch (e: Exception) { this.A = e; }",
            TsRawStatementNormalizer.Normalize("catch (e: Exception) { A = e; }", memberContext: ctx));
    }

    [Fact]
    public void New_And_InstanceOf_Type_Positions_Not_Prefixed()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "X" }, Array.Empty<string>());
        Assert.Equal(
            "new A(this.A);\nif (x instanceof A) { }",
            TsRawStatementNormalizer.Normalize("new A(A);\nif (x instanceof A) { }", memberContext: ctx));
    }

    [Fact]
    public void Already_Qualified_References_Not_Doubled()
    {
        var ctx = MemberCtx("AlgorithmBase", new[] { "threadStaticCancelToken", "cancelToken" }, new[] { "threadStaticCancelToken" });
        Assert.Equal(
            "this.cancelToken = AlgorithmBase.threadStaticCancelToken;",
            TsRawStatementNormalizer.Normalize("this.cancelToken = AlgorithmBase.threadStaticCancelToken;", memberContext: ctx));
    }

    [Fact]
    public void Type_Parameters_Never_Prefixed()
    {
        var ctx = MemberCtx("Sample", new[] { "T" }, Array.Empty<string>(), new[] { "T" });
        Assert.Equal(
            "var x = T;\nreturn x;",
            TsRawStatementNormalizer.Normalize("var x = T;\nreturn x;", memberContext: ctx));
    }

    [Fact]
    public void Strings_Comments_And_Object_Keys_Not_Prefixed()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "x" }, Array.Empty<string>());
        var input = "console.log(\"A\"); // A\n/* A */ A = 1;\nvar o = { x: A };";
        var expected = "console.log(\"A\"); // A\n/* A */ this.A = 1;\nvar o = { x: this.A };";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input, memberContext: ctx));
    }

    [Fact]
    public void Ctor_Raw_Body_Static_Reference_Qualified_And_Param_Shadowed()
    {
        var ctx = MemberCtx("BinaryHeapWithComparer", new[] { "A", "tClass" }, new[] { "initialHeapCapacity" }, new[] { "T" });
        var locals = new HashSet<string> { "tClass" };
        var input = "this.A = (TypeHelper.newArrayInstance(tClass, initialHeapCapacity + 1) as T[]);";
        var expected = "this.A = (TypeHelper.newArrayInstance(tClass, BinaryHeapWithComparer.initialHeapCapacity + 1) as T[]);";
        Assert.Equal(expected, TsRawStatementNormalizer.Normalize(input, memberContext: ctx, extraLocals: locals));
    }

    [Fact]
    public void Ternary_Operands_Get_Prefixes_When_Members()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "B" }, Array.Empty<string>());
        Assert.Equal(
            "x = cond ? this.A : this.B;",
            TsRawStatementNormalizer.Normalize("x = cond ? A : B;", memberContext: ctx));
    }

    [Fact]
    public void Declarator_Type_Annotations_Never_Prefixed()
    {
        // MSAGL Curve.cs "Color Color" pattern: property name == nested type name.
        // "let pBoxNode: ParallelogramNodeOverICurve = ..." -- the annotation is a
        // type position and must stay bare (prefixed output is a TS syntax error).
        var ctx = MemberCtx("Curve", new[] { "ParallelogramNodeOverICurve", "pBoxNode" }, Array.Empty<string>());
        Assert.Equal(
            "let pBoxNode: ParallelogramNodeOverICurve = edge.getUnderlyingPolyline().createCurve();",
            TsRawStatementNormalizer.Normalize("let pBoxNode: ParallelogramNodeOverICurve = edge.getUnderlyingPolyline().createCurve();", memberContext: ctx));
    }

    [Fact]
    public void Generic_Type_Arguments_Never_Prefixed()
    {
        // MSAGL RouterBetweenTwoNodes: Polyline is both a property and the element
        // type of CsDictionary<Point, Polyline> -- type arguments are type positions.
        var ctx = MemberCtx("RouterBetweenTwoNodes", new[] { "Polyline", "pointsToObstacles", "Point", "A", "B" }, Array.Empty<string>());
        Assert.Equal(
            "this.pointsToObstacles = new CsDictionary<Microsoft.Msagl.Core.Geometry.Point, Polyline>();",
            TsRawStatementNormalizer.Normalize("pointsToObstacles = new CsDictionary<Microsoft.Msagl.Core.Geometry.Point, Polyline>();", memberContext: ctx));
        Assert.Equal(
            "f<A, B>(1);",
            TsRawStatementNormalizer.Normalize("f<A, B>(1);", memberContext: ctx));
    }

    [Fact]
    public void Nested_Type_Names_Never_Prefixed_Even_In_Expressions()
    {
        // MSAGL Curve: ParallelogramNodeOverICurve is a nested type (emitted as a
        // top-level imported class) AND an instance property. Bare references are
        // type-qualified static calls ("Type.createX()"), not property access.
        var ctx = MemberCtx("Curve", new[] { "ParallelogramNodeOverICurve", "nn0" }, Array.Empty<string>(), typeNames: new[] { "ParallelogramNodeOverICurve" });
        Assert.Equal(
            "let nn0: ParallelogramNodeOverICurve = ParallelogramNodeOverICurve.createParallelogramNodeForCurveSeg(l0);",
            TsRawStatementNormalizer.Normalize("let nn0: ParallelogramNodeOverICurve = ParallelogramNodeOverICurve.createParallelogramNodeForCurveSeg(l0);", memberContext: ctx));
    }

    [Fact]
    public void Typed_Lambda_Params_Shadow_Members()
    {
        var ctx = MemberCtx("Sample", new[] { "a", "A" }, Array.Empty<string>());
        Assert.Equal(
            "(a: number) => a + this.A",
            TsRawStatementNormalizer.Normalize("(a: number) -> a + A", memberContext: ctx));
    }

    [Fact]
    public void Multi_Declarator_Second_Annotation_Never_Prefixed()
    {
        var ctx = MemberCtx("Sample", new[] { "A", "B", "Curve", "a", "b" }, Array.Empty<string>());
        Assert.Equal(
            "let a: number = this.A, b: Curve = this.B;",
            TsRawStatementNormalizer.Normalize("let a: number = A, b: Curve = B;", memberContext: ctx));
    }

    [Fact]
    public void Comparison_In_Initializer_Still_Prefixes()
    {
        // Guard: "i < j" inside an initializer is a comparison, not generic type
        // arguments; both operands are expressions and still get prefixes.
        var ctx = MemberCtx("Sample", new[] { "i", "j", "swapped" }, Array.Empty<string>());
        Assert.Equal(
            "let swapped = this.i < this.j;",
            TsRawStatementNormalizer.Normalize("let swapped = i < j;", memberContext: ctx));
    }

    [Fact]
    public void Self_Typed_Member_Followed_By_Dot_Is_Type_Qualified_Static_Access()
    {
        // MSAGL "Color Color": property SolvingMethod of type SolvingMethod (enum).
        // "SolvingMethod.PrecondConjugateGradient" is a type-qualified static enum
        // access in C# and must stay bare (prefixing yields TS2339/TS2576).
        var ctx = MemberCtx("StressMajorizationSettings", new[] { "SolvingMethod", "UpdateMethod" }, Array.Empty<string>(), selfTyped: new[] { "SolvingMethod", "UpdateMethod" });
        // Field-initializer literal shape (the raw text the emitter normalizes).
        Assert.Equal(
            "SolvingMethod.PrecondConjugateGradient",
            TsRawStatementNormalizer.Normalize("SolvingMethod.PrecondConjugateGradient", memberContext: ctx));
        // Value position (property read) still gets the this. prefix.
        Assert.Equal(
            "x = this.SolvingMethod;",
            TsRawStatementNormalizer.Normalize("x = SolvingMethod;", memberContext: ctx));
        // Non-self-typed member followed by a dot is a plain instance access (A.length).
        var ctx2 = MemberCtx("BinaryHeapWithComparer", new[] { "A" }, Array.Empty<string>());
        Assert.Equal(
            "this.A.length",
            TsRawStatementNormalizer.Normalize("A.length", memberContext: ctx2));
    }

    [Fact]
    public void Self_Typed_Member_Generic_Array_Type_Still_Not_Confused()
    {
        // int[] / CSharpList<T> typed members are not self-typed: prefixes apply.
        var ctx = MemberCtx("Sample", new[] { "A", "items" }, Array.Empty<string>());
        Assert.Equal(
            "this.A.length + this.items.size()",
            TsRawStatementNormalizer.Normalize("A.length + items.size()", memberContext: ctx));
    }

    // ─── Round 19（MSAGL TS2503/TS2304）：FQCN 泛型实参位 + 字段初始化器位 ──

    [Fact]
    public void Fqcn_Generic_Argument_Position_Is_Shortened()
    {
        // Round 19（MSAGL TS2503 156 条）：NormalizeFqcnExpressions 只有 after=='('
        // 与 after=='.' 分支，泛型实参位（after=='>'）直通 ——
        // "new CSharpSortedDict<Double, Microsoft.Msagl.Core.Geometry.Point>()"（Curve.ts:1593）
        // "new CSharpKeyValuePair<Double, Microsoft.Msagl.Core.Geometry.Point>(...)"（EdgeLabelPlacement.ts:504）
        // 链末段是类型名（非方法名），按类型规则整链缩短。
        Assert.Equal(
            "dict = new CSharpSortedDict<Double, Point>();",
            NormalizeWithTable("dict = new CSharpSortedDict<Double, Microsoft.Msagl.Core.Geometry.Point>();"));
        Assert.Equal(
            "list.push(new CSharpKeyValuePair<Double, Point>(start, startPoint));",
            NormalizeWithTable("list.push(new CSharpKeyValuePair<Double, Microsoft.Msagl.Core.Geometry.Point>(start, startPoint));"));
        // 非末位泛型实参（后继 ','）同样缩短
        Assert.Equal(
            "p = new CSharpPair<Point, Station>(a, b);",
            NormalizeWithTable("p = new CSharpPair<Microsoft.Msagl.Core.Geometry.Point, Station>(a, b);"));
    }

    [Fact]
    public void Fqcn_Generic_Argument_Java_Prefix_Is_Not_Shortened()
    {
        // 守卫（与 Round 18 一致）：java./io.github./System. 前缀经 mapType 恒等返回，
        // 泛型实参位（after=='>'）同样不受影响。
        Assert.Equal(
            "x = new CSharpSortedDict<Double, java.math.BigDecimal>();",
            NormalizeWithTable("x = new CSharpSortedDict<Double, java.math.BigDecimal>();"));
        // 值位裸链（after==','）走同一类型规则：mapType 末段映射（该形态在 TS 中
        // 本身即非法——未知链缩短不引入新错误行）
        Assert.Equal(
            "y = foo(Value, 5);",
            NormalizeWithTable("y = foo(Microsoft.Unknown.Ns.Value, 5);"));
    }

    // ─── Round 21（MSAGL TS2304 1,392）：CSharpGenericIterable.from(x) 值位包装剥除 ──

    [Fact]
    public void GenericIterable_From_Wrapper_Is_Dropped()
    {
        // C# IEnumerable<T> 值生产：前端产出 Java 兼容包装 CSharpGenericIterable.from(x)
        //（StatementTransformer.ExpressionAndReturn.cs / ArgumentTransformer.cs）。
        // TS 中 Iterable<T> 是结构类型，x 本身即可迭代，包装是无操作——剥除后
        // 类型注解 Iterable<T> 与值位 x 完全一致。
        Assert.Equal(
            "return this.clusterHierarchies;",
            TsRawStatementNormalizer.Normalize("return CSharpGenericIterable.from(this.clusterHierarchies);"));
        Assert.Equal(
            "return convexHull.calculate();",
            TsRawStatementNormalizer.Normalize("return CSharpGenericIterable.from(convexHull.calculate());"));
    }

    [Fact]
    public void GenericIterable_From_Nested_Wrappers_Are_All_Dropped()
    {
        // RTree.ts:97 嵌套形态（外层包装内层包装）：全部剥除至裸表达式。
        Assert.Equal(
            "return (cond ? a : x);",
            TsRawStatementNormalizer.Normalize("return CSharpGenericIterable.from((cond ? a : CSharpGenericIterable.from(x)));"));
        // 同行多个包装（sibling）：各自剥除。
        Assert.Equal(
            "return a + b;",
            TsRawStatementNormalizer.Normalize("return CSharpGenericIterable.from(a) + CSharpGenericIterable.from(b);"));
    }

    [Fact]
    public void GenericIterable_From_Non_Noop_Wrappers_Are_Not_Touched()
    {
        // 守卫：CSharpGenericEnumerator.from(x.iterator()) 包装产物后续使用
        // moveNext()/getCurrent()，剥除会使调用点失去枚举器语义——原样保留。
        Assert.Equal(
            "let outEnum: CSharpGenericEnumerator<TEdge> = CSharpGenericEnumerator.from(graph.outEdges(vertex).iterator());",
            NormalizeWithTable("let outEnum: CSharpGenericEnumerator<TEdge> = CSharpGenericEnumerator.from(graph.outEdges(vertex).iterator());"));
        // 守卫：字符串字面量内含包装文本不被改写
        Assert.Equal(
            "console.log(\"CSharpGenericIterable.from(x)\");",
            TsRawStatementNormalizer.Normalize("console.log(\"CSharpGenericIterable.from(x)\");"));
        // 守卫：instanceof 类型位不是值位包装（外层 .from 仍是无操作剥除；
        // 残留 instanceof 类型位属类型映射下轮候选）。
        Assert.Equal(
            "return (feedbackSet instanceof CSharpGenericIterable ? (feedbackSet) : null);",
            TsRawStatementNormalizer.Normalize("return CSharpGenericIterable.from((feedbackSet instanceof CSharpGenericIterable ? (feedbackSet) : null));"));
    }

    [Fact]
    public void GenericICollection_FromTyped_Wrapper_Is_Dropped()
    {
        // C# ICollection<T> 值生产（InitialLayoutByCluster.ts:387/409）：
        // CSharpICollection.fromTyped(x) 与 Iterable 包装同源同语义，剥除。
        Assert.Equal(
            "self.clusters = Enumerable.toList(ArrayHelper.toList([graph.getRootCluster()]));",
            TsRawStatementNormalizer.Normalize("self.clusters = CSharpICollection.fromTyped(Enumerable.toList(ArrayHelper.toList([graph.getRootCluster()])));"));
    }

}
