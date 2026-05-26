# Struct→Java Class Null Fix — Implementation Plan

**Goal:** Fix two manifestations of the C# struct→Java class null issue that cause NPE in generated GPLEX/ShiftReduceParser code:

1. **Bug 1a**: `AbstractScanner<TValue>.yylval` defaults to null when `TValue` is bound to a struct type
2. **Bug 1b**: `default(TValue)` in `ShiftReduceParser.Reduce()` is translated as `null` instead of a new struct instance

**Hard constraints**: Do not modify C# source. Do not modify generated Java. Fix must be in the converter.

---

## Architecture

### Core Strategy: TypeParameterBindingAnalyzer

Add a pre-analysis pass that walks the full project compilation to classify each type parameter of each generic class:

- **AlwaysStruct**: All subclass bindings are to structs → emit `new TypeName()` for defaults
- **AlwaysReference**: All bindings are to reference types → emit `null` (current behavior)
- **Mixed**: Different subclasses use different kinds → require factory method injection

This analysis runs once per compilation and is cached for O(1) lookups during conversion.

### MSAGL Outcome

All subclasses of `AbstractScanner<TValue>` and `ShiftReduceParser<TValue>` bind `TValue` to `ValueType` (a struct). The analyzer classifies both as `AlwaysStruct`, resulting in:
- `= new ValueType()` field initializers for `yylval`
- `new ValueType()` for `default(TValue)` expressions

---

## Task 1: Add TypeParameterBindingAnalyzer

**Files:**
- New: `src/CSharpToJava.Core/Analysis/TypeParameterBindingAnalyzer.cs`

**Steps:**
- Walk all generic type declarations in the compilation
- For each type parameter, find all subclass/base call instantiations
- Classify each type parameter as `AlwaysStruct`, `AlwaysReference`, or `Mixed`
- Cache results indexed by `(genericTypeSymbol, typeParamName)`

---

## Task 2: Fix Bug 1a — Field Initializer for Generic Struct Fields

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`

**Steps:**
- When a field has an `ITypeParameterSymbol` type with no initializer:
  - Query the `TypeParameterBindingAnalyzer`
  - If `AlwaysStruct`: emit `= new TypeName()` as field initializer
  - If `Mixed`: defer to Task 5 (subclass constructor injection)
  - If `AlwaysReference`: keep current null-default behavior
- Existing non-generic struct field initialization (commit `003f178`) is unchanged

---

## Task 3: Fix Bug 1b — `default(TValue)` Translation

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`

**Steps:**
- Extend `GetDefaultValueForType` to accept `ConversionContext`
- For unconstrained `ITypeParameterSymbol`:
  - Query analyzer → `AlwaysStruct` → emit `new T()`
  - Query analyzer → `AlwaysReference` → emit `null`
  - Query analyzer → `Mixed` → emit `createDefaultT()` (factory method call)
- The existing `where T : struct` constraint path is unchanged

---

## Task 4: Add Converter Regression Tests

**Files:**
- Modify: `tests/CSharpToJava.Tests/StructTransformerTests.cs`

**Steps:**
- Test: Field of struct-typed generic parameter gets `= new StructType()` initializer
- Test: `default(TValue)` where TValue is a struct emits `new ValueType()` not `null`
- Test: Generic field where type param is always a reference type keeps `null` default
- Test: Generic field where type param is mixed (pending factory method pattern)

---

## Task 5: Handle Mixed Bindings (Factory Method Pattern)

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`

**Steps:**
- For each generic class with `Mixed` type parameters:
  - Generate a protected `createDefaultT()` method in the generic base class
  - Override in each concrete subclass to return `new ConcreteType()` or `null` as appropriate
- This handles the edge case where a generic class has different struct/class instantiations across subclasses

---

## Verification

1. Run converter tests: `dotnet test --filter "StructTransformerTests"` → all pass
2. Re-convert MSAGL: `E:\agl-master\GraphLayout\` → clean output
3. Run `SugiyamaLayoutTests#randomDotFileTests` → passes (no NPE)
4. Run full converter test suite → no regressions

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Type parameter symbol not available in HIR | Low | Analyzer runs before conversion using Roslyn symbols directly |
| Circular generic type references | Very Low | Guard with visited set in analyzer |
| Performance impact of pre-analysis | Low | Cache results; analysis is O(n) in type count |
