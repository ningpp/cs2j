# C15 numeric-boundary M2/M3/M4 实施记录

**日期**：2026-09-30
**分支/worktree**：`feature/preproc-c15-checked-unchecked` @ `.worktrees/preproc-c15-checked-unchecked`（M1 基线 1ec1845d）
**设计**：[2026-09-28-c15-checked-unchecked-design.md](file:///d:/code/cs2j/.worktrees/preproc-c15-checked-unchecked/docs/superpowers/specs/2026-09-28-c15-checked-unchecked-design.md)
**约束**：不改 C#→Java 转换器代码；分类只用 SemanticModel；幂等；干净文件字节级一致并保持 BOM；不新建仓库内支撑工程。

---

## 1. 里程碑与交付物

### M2 — checked 显式化 + 双侧支撑库

- **Core `NumericBoundary/`**
  - `CheckedExprRewriter`：checked 二元算术（`Add/Subtract/MultiplyChecked{Int32,Int64,UInt32,UInt64}`，`/ %` 与位运算剥壳不插 helper）、一元负号（仅有符号域；`-uint` 穿透 IConversionOperation 识别后保留裸 `-a`）、checked 窄化转换（29 个 `To<T>Checked<Src>`，宽化返回 null）、复合赋值（窄目标按 Int32 提升域 + 出口转换双检查）、`++/--`（standalone 直赋值；值消费 hoist，Block/SwitchSection 交错展开；属性/索引器等 5324）、enum 自增按底层域并 cast 回 `(global::E)`、lambda 重置上下文/本地函数按声明位置继承。
  - 编译期常量：`HasCompileTimeConstantValue` 守卫——折叠常量要么 NB-6 物化、要么保留折叠，永不插运行时 helper；隐式窄化折叠（如 /checked 下 `byte b = 200+100;`）按 ConvertedType 物化为 unchecked wrap 值 `(byte)44`，保证中间树可编译。
- **双侧支撑库**
  - [UnsignedMath.cs](file:///d:/code/cs2j/.worktrees/preproc-c15-checked-unchecked/src/Cs2jUnsignedMath/UnsignedMath.cs)：checked 算术（四域；UInt64 无 negate）+ 29 件 `checked((T)v)` 转换。
  - [UnsignedMath.java](file:///d:/code/cs2j/.worktrees/preproc-c15-checked-unchecked/java/csharptojava-compat/src/main/java/io/github/ningpp/compat/UnsignedMath.java)：camelCase 同名；有符号委托 `Math.*Exact`；UInt32 用 long 进位检查；UInt64 用 `Long.compareUnsigned` + BigInteger；转换族 `checkedRange`/`checkedRangeUnsignedSource`；违例一律 `throw new ArithmeticException("overflow")`。
  - `UnsignedMathTest.java` 新增 6 组 checked 用例（算术边界、in-range 转换、overflow 转换）。

### M3 — 闸门、工程级 /checked、共处

- **NB-12**：任一 Error 诊断 / 零有效改动 / 格式化失败 → 整文件回滚原文（`FilesDirtyNoRewrite`）。
- **NB-13**：输出树零 unchecked 残留；checked 残留与 `CheckedUserOpShellsDeferred` 对账，voucher 仅当 EnabledPassIds 含 `member-desugar` 时豁免，否则 CS2J5327 Error（strict）。
- **工程级 `/checked`**：`ParseCheckForOverflowUnderflow` 目录映射逐文件透传；AfterFiles 阶段从中间树 csproj 中**剥离 `CheckForOverflowUnderflow` 元素**（语义全部转入 helper）；仅剥离不引 helper 时不释放库；无宿主工程/无 restore 目标报 CS2J5309 Warning。
- **共享注入**：`Cs2jUnsignedMathInjection.ReleaseAndInjectAsync`（numeric-boundary 与 unsigned 共用）；TFM<net10 以 5310 Warning 聚合。
- **member-desugar 共处**：`operator checked +`（WideUserOps）计数 voucher，单跑 numeric-boundary → CS2J5327；双 pass 共处 → 壳保留、零 Error。
- **错误码每例单测**：5320 nint、5321 double→int、5322 dynamic、5323 表达式树、5324 属性自增/字段初始化器、5325 lifted、5327 voucher strict。

### M4 — 默认开启与全链路

- `PassCatalog`/`PassChainFactory`：numeric-boundary `defaultEnabled: true`、legacy toggle `false→true`；EnabledPassIds 经 CliPreprocessChain → options 透传。
- `preprocess --list-passes` 实测：`numeric-boundary  Implemented  on`。
- C#→Java 端到端冒烟（TEMP 工程 + convert-project + compat classpath javac/java）：
  `addCheckedInt32/toByteCheckedInt32/negateCheckedInt32/addCheckedUInt32` 全部映射为 camelCase 调用；`catch (OverflowException)` 映射为 `catch (ArithmeticException)`；运行输出 `42 / caught: integer overflow / 200 / -42 / 4294967285 / caught: overflow`。

## 2. Fixture（tests/）

| Fixture | 用途 | 运行时证据 |
|---|---|---|
| `SampleNumericBoundarySolution`（Lib+App） | 全量 helper 化样例：四域算术、negate、-uint、窄化（int/ulong 源）、checked 块 foreach 复合、byte 复合、前缀/后缀、enum 自增；unchecked 剥离/嵌套/decimal/常量 | Exe 自检输出 `numeric-boundary sample OK`，溢出路径捕获 OverflowException |
| `SampleNumericBoundaryCheckedSolution` | csproj `/checked` 正例：标志被剥离、库释放注入、foreach `+=` 变 helper | `dotnet run` 先输出 `6`，随后非零退出 + `System.OverflowException` |
| `SampleNumericBoundaryUnsupportedSolution` | `dynamic` + `checked((int)d)` 反例 | Success=false（CS2J5322），文件原样回滚 |

## 3. 验证证据（2026-09-30，JAVA_HOME=jdk-28-ea-11）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 解决方案构建 | `dotnet build CSharpToJavaConverter.slnx -c Release -t:Rebuild` | **0 错误 / 99 警告**（与基线一致） |
| 核心单测 | `dotnet test tests/CSharpToJava.Tests -c Release` | **2103 通过** |
| 工程/fixture 测试 | `dotnet test tests/CSharpToJava.ProjectTests -c Release` | **683 通过**（含 NumericBoundaryRewriterUnitTests 64、NumericBoundaryProjectTests 3） |
| 转换测试 | `dotnet test tests/CSharpToJava.ConversionTests -c Release` | **671 通过** |
| Java compat | `mvn -f java/csharptojava-compat/pom.xml test` | **1584 通过**（EXIT=0） |
| Java 端到端 | convert-project + `javac --release 28 --enable-preview` + `java` | 见 §1 M4 输出 |

新增/改写测试：NumericBoundaryRewriterUnitTests 64 个（重写器规则全量）、NumericBoundaryProjectTests 3 个（含真实 restore/build/run）、compat `UnsignedMathTest` +6。

## 4. 外部门禁（未执行，如实登记）

- **SDK 训练集扫描**：仓库中未发现 `prompts/preprocessor-status.md`，`tools/` 下亦无训练集扫描入口/语料路径配置，无法在本环境执行，未伪造结果。需要外部 SDK 语料库与对应扫描脚本，建议合入后由持有语料的环境补跑。

## 5. 已知边界（后续族任务，非本任务范围）

- C17（位运算/移位 checked 语义复核）、C22、C34 decimal 运算族、C41 特殊数值（Half/Int128/UInt128/nint 现显式 5320 拒绝）沿用本 pass 基建增量扩展。
- 生成 Java 中支撑库以 `Cs2jUnsigned.UnsignedMath` 包名寻址，与既有 unsigned pass 的 Java 对接方式相同（compat 实际包 `io.github.ningpp.compat`，映射/打包收口是 unsigned 族既存的独立事项）。
