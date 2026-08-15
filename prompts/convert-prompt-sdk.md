# C# → Java 转换器：逐错迭代修复循环

## 目标
使用本项目的 C#→Java 转换器，将 `D:\csharp-sdk` 转换为 `d:\cs-java-816-sdk`，并通过迭代修复**转换器代码**（而非生成的 Java 代码），直到生成的 Java 项目能通过 `mvn clean install -e` 零错误编译。

## 硬性约束
1. **严禁修改** `D:\csharp-sdk` 下的任何文件（C# 源码只读）
2. **严禁手动编辑** `d:\cs-java-816-sdk` 下的任何文件（所有修复必须通过改进转换器来生效）
3. **严格按 Step 1→8 顺序执行**，不可跳步或颠倒
4. **每个 Step 5 必须有红→绿验证**（先确认测试失败，再确认修复后通过）
5. **每次 `mvn clean install -e` 必须保存完整日志**
6. compat 库源代码`D:\code\cs2j\java\csharptojava-compat`
7. **mvn 本地仓库目录** `D:\mvnrepo`

---

## 迭代循环（重复直到 BUILD SUCCESS）

### Step 1 — C# → Java 转换
```bash
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s D:\csharp-sdk -d d:\cs-java-816-sdk
```
确认转换命令本身无异常退出。

### Step 2 — Maven 编译 & 日志保存
```bash
cd d:\cs-java-816-sdk ; mvn clean install -e > mvn-build.log 2>&1
cat mvn-build.log
```
检查退出码：`echo $?`。若为 0 且输出包含 `BUILD SUCCESS` → **退出循环，任务完成**。

### Step 3 — 提取第一个编译错误
从 `mvn-build.log` 中定位**第一个** `[ERROR]` 编译错误，提取以下信息：
- **文件**：哪个 `.java` 文件
- **行号**：精确行号
- **错误类型**：如 `cannot find symbol`、`incompatible types` 等
- **出错代码片段**：错误行及其上下 64 行

将这些信息**追加**写入 `d:\code\cs2j\sdk-error-analysis.md`：
```markdown
## Iteration N — <错误类型简写>
- **Java 文件**: <path>
- **行号**: <N>
- **错误信息**: <exact message>
- **代码片段**:
  ```java
  <snippet>
  ```
```

### Step 4 — 写失败的单元测试（Red）
1. 在 `tests/CSharpToJava.Tests/` 下找到或新建对应的测试文件
2. 添加一个最小 C# 代码片段来复现该 bug 模式
3. 运行 `dotnet test -c Release --filter "FullyQualifiedName~<TestName>"` — **必须确认测试失败**
4. 若测试意外通过，说明不是该场景，回到 Step 5 重新分析

### Step 5 — 系统性根因分析
1. **反向定位 C# 源文件**：从 Java 文件的包名/类名反查对应的 C# 文件
2. **识别转换器组件**：用 `Grep` 搜索相关 transformer / type mapping / lowering pass
3. **判定根因类别**（择一）：
   - 类型映射缺失（`TypeMappings.json`）
   - Transformer 逻辑缺陷（`Transformers/` 下某文件）
   - Lowering pass 不足（`Lowering/` 下某文件）
   - 语义模型信息丢失（`ConversionContext`）
4. 将根因分析写入 `sdk-error-analysis.md` 同一条目下：
   ```markdown
   - **对应 C# 文件**: <path>
   - **根因分类**: <类型映射缺失 | Transformer | Lowering | 语义丢失>
   - **涉及组件**: <具体文件路径>
   - **分析**: <一句话说明为什么转换器产出了错误代码>
   ```
5. 若定位困难，用 `Explore` sub-agent 搜索代码库

### Step 6 — 修复转换器代码（Green）
1. 修改对应的转换器组件（仅改 `d:\code\cs2j` 下的代码）
2. 运行 `dotnet build -c Release` 确认编译通过
3. 运行 `dotnet test -c Release --filter "FullyQualifiedName~<TestName>"` — **必须确认测试通过**
4. 运行 **全量测试** `dotnet test -c Release` — 确保无回归

### Step 7 — 验证修复 & Commit
1. 回到 Step 1 重新转换
2. 回到 Step 2 重新编译 — 确认**本轮迭代的错误已消失**
3. Commit 代码变更：
   ```
   git add -A
   git commit -m "fix: <一句话描述修复内容>

   Root cause: <根因简述>
   ```
4. 更新 `sdk-error-analysis.md` 中本条目状态为 `✅ Fixed`

### Step 8 — 继续下一轮
进入下一轮迭代（Step 1→2→3...），直到 Step 2 输出 `BUILD SUCCESS`。

---

## 成功标准
`d:\cs-java-816-sdk` 目录下执行 `mvn clean install -e` 返回零错误、`BUILD SUCCESS`，且 `d:\code\cs2j` 的全量单元测试全部通过。