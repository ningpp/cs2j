# Findings: MSAGL Conversion Research

## Project Structure
- Source: C:\automatic-graph-layout-master\GraphLayout\MSAGL
- Project file: AutomaticGraphLayout.csproj
- File count: 492 .cs files
- Subdirs: Core, DebugHelpers, GraphmapsWithMesh, Layout, Miscellaneous, Routing
- Destination: C:\agl\v20260314 (with Maven structure: src/main/java)

## Converter Tool
- CLI at: d:\code\CSharpToJavaConverter\src\CSharpToJava.CLI\Program.cs
- Java25 is supported as a java-version value
- Maven POM generation is enabled via --generate-pom flag (default = true)
- Generated structure: destination/src/main/java/... (when --generate-pom)
- pom.xml is generated at destination root

## Java Environment
- TBD (check with `java -version`)

## Compilation Error Patterns (v20260315 — 200 errors in 14 files)

Full error log saved to: `C:\agl\v20260315\compile_errors_full.txt`

| Pattern | Example | Count | Root Cause |
|---------|---------|-------|------------|
| Static call on generic type | `Set<T>.valueEquals(x, null)` | ~80 | Converter emits generic receiver for static calls |
| Jagged array dimension order | `new double[][2][]` | ~40 | Semantic ElementType includes nested [] |
| Primitive type static members | `double.MaxValue`, `double.IsInfinity(x)` | ~40 | Primitive keyword used instead of boxed class name |
| Explicit generic method call | `CrossRectangleNodes<TA,P>(a, b, c)` | ~40 | Type args emitted on standalone method call |

### Files with errors (14):
- AlgorithmBase.java — pattern 1
- Set.java — pattern 1
- PenetrationDepth.java — pattern 3
- PlaneTransformation.java — pattern 2
- MultidimensionalScaling.java — pattern 2
- OptimalColumnPacking.java — pattern 3
- OptimalPacking.java — pattern 3
- OptimalRectanglePacking.java — pattern 3 (inferred)
- OverlapRemovalCluster.java — pattern 3 (inferred)
- OverlapRemovalNode.java — pattern 3
- RectangleNode.java — pattern 1
- RectangleNodeUtils.java — pattern 4
- RTree.java — pattern 1
- BasicGraphOnEdges.java — pattern 2

## Fixes Applied to Converter
(To be filled as fixes are made)
