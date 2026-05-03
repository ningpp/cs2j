# Fix Project-Level Conversion Errors in C# to Java Converter

**Date**: 2026-05-03
**Status**: Design approved, awaiting implementation

## Problem

When converting the MSAGL C# project to Java using `convert-project`, the resulting Java code has 76 compilation errors across 6 root causes. Several of these errors only manifest in project-level conversion (not single-file conversion) because cross-file type resolution behaves differently with in-memory syntax trees vs. disk-based Roslyn compilations.

## Root Cause Groups

### RC1: `!int` unary operator not rewritten in IR pipeline (2 errors)

**Files**: LgInteractor.java, OverlapRemovalFixedSegmentsMst.java
**Symptom**: `一元运算符 '!' 的操作数类型int错误` (unary '!' operand type int error)

**Root cause**: `UnaryExpressionTransformer.TransformToIR()` ([UnaryExpressionTransformer.cs:56-111](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs#L56-L111)) maps `LogicalNotExpression` to `("!", false)` with **no operand type check** (line 63). The old string-based `TransformLogicalNot()` (lines 154-168) correctly checks the operand type and rewrites `!nonBoolExpr` to `(expr == 0)`, but the IR path bypasses this entirely. The project pipeline uses `TransformToIR()` for structured IR generation, while single-file conversion may use the string-based `Transform()` path more often.

**Fix**: In `TransformToIR()`, after determining `op == "!"`, check the operand type via `context.SemanticModel.GetTypeInfo()`. If not confirmed `System_Boolean`, fall back to the raw string path (`JavaRawExpression`) which calls `TransformLogicalNot()`.

### RC2: `Stream.size()` — LINQ Count() always maps to `.size()` (2 errors)

**Files**: LayoutAlgorithmHelpers.java
**Symptom**: `找不到符号 方法 size()` (cannot find symbol: method size() on Stream)

**Root cause**: [InvocationExpressionTransformer.cs:425-426](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs#L425-L426) always maps LINQ `.Count()` to `.size()`. After LINQ operations like `Union`/`Intersect` that produce `java.util.stream.Stream` (not `Collection`), Java requires `.count()`. The same mapping also exists as a last-resort fallback in [IdentifierExpressionTransformer.cs:957](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L957).

**Fix**: Before emitting `.size()` for `Count`, check the return type of the receiver. If the Java-mapped type is `java.util.stream.Stream`, emit `.count()`. Detection: try semantic type info on the receiver, then check if the translated receiver string indicates a stream operation (`.distinct()`, `.filter(`, `.map(`).

### RC3: Type/Property name collision — semantic model resolves property name to type (18+ errors)

**Files**: PolyIntEdge.java, Anchor.java, LinkedPoint.java, CdtFrontElement.java, PerimeterEdge.java
**Symptom**: `无法从静态上下文中引用非静态方法`, `找不到符号: 变量 Node`, `无法从静态上下文中引用非静态变量 X`

**Root cause**: Properties like `Edge`, `Point`, `Anchor`, `Node` share names with types in the same namespace (e.g., `Microsoft.Msagl.Core.Layout.Edge`). In [IdentifierExpressionTransformer.cs:446-570](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs#L446-L570) (the 8 receiver type resolution strategies), when resolving `Edge.getLabel()`:

1. The semantic model `GetSymbolInfo()` resolves `Edge` to the **type** `Microsoft.Msagl.Core.Layout.Edge`
2. The converter emits the fully-qualified type name as the receiver: `Microsoft.Msagl.Core.Layout.Edge.getLabel()`
3. Java interprets this as a static method call on class `Layout.Edge`, producing both "non-static from static context" AND "class Layout not found" errors

The correct conversion is `getEdge().getLabel()` — recognizing that `Edge` is an instance property on `this`.

**Fix**: After the existing 8 receiver resolution strategies, add a **9th strategy**: when the resolved receiver is a type symbol and the access expression is a simple `IdentifierNameSyntax`, check if the enclosing type has a member (property/field) with the same name as the identifier. If so, emit `this.get{Name}()` (instance property access) instead of `{Type}.{method}()` (static type reference). This strategy should check:
1. The enclosing type's immediate members for a property/field matching the identifier text
2. Base class members if not found on the enclosing type

### RC4: `System.Xml` namespace not mapped (2 errors)

**Files**: GeometryGraphWriter.java, GeometryGraphReader.java
**Symptom**: `找不到符号: 变量 Xml` (cannot find symbol: variable Xml at System class)

**Root cause**: C# types from `System.Xml` (XmlWriter, XmlWriterSettings, XmlConvert, XmlReader) are not recognized in the type mapping system. The converter emits them as-is: `XmlWriter.create(...)`, `XmlConvert.toString(...)`, etc. Java interprets `System.Xml.XmlWriter` as `java.lang.System.Xml.XmlWriter` (static field `Xml` of `java.lang.System`), which doesn't exist.

**Fix**: Add `System.Xml` types to the type mapping configuration (`TypeMappings.json`). Minimal fix:
1. Add `System.Xml` → `System.Xml` namespace mapping (preserves namespace, prevents collision with `java.lang.System`)
2. Add basic type mappings for the specific types used: `XmlWriter`, `XmlWriterSettings`, `XmlConvert`, `XmlReader`
3. Ensure these types are added to the import block as `System.Xml.*` rather than being resolved against `java.lang.System`

Note: This is a namespace collision fix, not a full API mapping. The converted Java code will still reference `System.Xml.XmlWriter` etc., which won't compile against standard Java. A broader solution (mapping to `javax.xml.stream.*`) is deferred to a separate design.

### RC5: `getCurrent()` — `IsEnumeratorLikeType` doesn't match Iterator types (8 errors)

**Files**: NetworkSimplex.java
**Symptom**: `找不到符号: 方法 getCurrent()` (cannot find symbol: method getCurrent() on Iterator)

**Root cause**: Four code paths in [IdentifierExpressionTransformer.cs](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs) map `IEnumerator.Current → next()`:
- Path 1: `TransformIdentifier` (lines 256-258)
- Path 2: `TransformMemberAccess` Fix 2 (lines 841-843)
- Path 3: `TransformMemberAccess` GetTypeInfo fallback (lines 868-869)
- Path 4: Last-resort unconditional (line 952)

All rely on `IsEnumeratorLikeType()` (lines 1150-1169) which **only** matches types named `IEnumerator` in `System.Collections`/`System.Collections.Generic`. When a C# class that implements `IEnumerator<T>` has its Java-mapped type become `Iterator<T>`, the semantic model may return the Java-mapped type (or fail to resolve), causing `IsEnumeratorLikeType` to return false. Paths 1-3 are skipped, and some paths (like `Count`/`Values`/`Keys` checks at lines 953-954) may preempt reaching the last-resort Path 4.

**Fix**: Expand `IsEnumeratorLikeType` to also check if the type's Java mapping (via `TypeMappingRegistry`) resolves to `java.util.Iterator` or `Iterator<T>`. Additionally, check if the type (or any of its interfaces) is named `Iterator` in `java.util`.

### RC6: `var` type loss — array access on List type (6 errors)

**Files**: SteinerCdt.java
**Symptom**: `需要数组, 但找到java.util.List<java.lang.String>` (array required but found List)

**Root cause**: `Regex.Split()` returns `string[]` in C#. The variable `lineParsed` is declared `var`. In [ElementAccessTransformer.cs:119](../../src/CSharpToJava.Core/Transformers/Expression/Transformers/ElementAccessTransformer.cs#L119), the `isArray` check uses `IArrayTypeSymbol` from the semantic model. If the semantic model can't resolve the type of the `var`-declared local (common after LINQ rewrite or in project pipeline), `exprType` is null/Error, and the fallback at line 181 defaults to `.get(idx)` (List access). But the type may actually be an array, which would need `[idx]`.

**Fix**: Add a heuristic fallback in `ElementAccessTransformer` when the type can't be resolved: check if the variable name appears in VarTypeMap with a known array type. Additionally, during the pre-scan phase (where `VarTypeMap` is populated), ensure `var`-declared locals whose initializer comes from a method returning an array type are recorded with their proper array types.

## Test Strategy

Each root cause gets a **converter-level unit test** before the fix:

1. **RC1**: Feed `int x = 5; if (!x) { }` through the IR pipeline → assert output is `(x == 0)` not `!x`
2. **RC2**: Feed `list.Union(other).Count()` → assert `.count()` not `.size()`
3. **RC3**: Feed a class with property `Edge` + method accessing `Edge.Curve` → assert `getEdge().getCurve()`
4. **RC4**: Feed `using System.Xml; var w = XmlWriter.Create(...);` → assert proper Java import
5. **RC5**: Feed class implementing `IEnumerator` with `.Current` access → assert `.next()` not `getCurrent()`
6. **RC6**: Feed `var arr = Regex.Split(s, pattern); var x = arr[0];` → assert `[0]`

Tests live in `tests/CSharpToJava.Tests/`, following the existing xUnit patterns.

## Implementation Order

1. **RC1** — Simplest fix, single file change, low risk
2. **RC2** — Simple type check before `.size()` emission
3. **RC5** — Expand type recognition, builds on existing paths
4. **RC3** — Most complex, new fallback strategy for type resolution
5. **RC6** — VarTypeMap improvement
6. **RC4** — Type mapping additions (or documented as known limitation)

## Notes

- After each RC is fixed, verify the corresponding errors in the full MSAGL conversion are resolved
- Remove any CLI text patches in `Program.cs` that target the fixed error patterns
- RC4 may require a broader design if full `System.Xml` API coverage is needed
