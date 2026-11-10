# cs2j 长期项目笔记

## 验证约定（用户明确要求）

- **不要运行 `tests/CSharpToJava.ProjectTests` 与 `tests/CSharpToJava.ConversionTests`
  的全量测试套件**，太慢。回归验证用 `tests/CSharpToJava.Tests` 全量 +
  定向 filter + 实际 `convert-project` 输出的 Java 文件逐字节比对。
- 根目录 `dotnet test` 只覆盖 `CSharpToJavaConverter.slnx` 里 5 个源码项目，
  **不包含测试项目**，不能当作全量测试通过的依据。
- 改动的正确性判据：`dotnet build` 0 警告 0 错误、主测试项目全量绿、
  全量转换 1986 个 .java 与基线逐字节一致；性能优化前后必须给出真实耗时 A/B。

## 工程习惯

- 测试先行（先写红灯测试）→ 单文件/单逻辑增量提交 → 每个提交写明确中文 commit message。
- 批量脚本改写 .cs 后要按 `git show HEAD:<file>` 的行尾（CRLF/LF）逐文件规范化，
  否则产生整文件噪音 diff。
- 性能诊断产物放 `build-logs/`：时间戳日志、`cs2j-pass-profile.json`、
  `dotnet-trace` speedscope（`build-logs/analyze_speedscope.py` 解析）。
