# Fix Default Parameter Handling for Constructors and Methods

## Summary

C# supports default parameter values in both methods and constructors. Java does not.
The old pipeline's `MethodTransformer` already generates overloads for methods with
defaults, but `ConstructorTransformer` is missing this logic entirely. Additionally
the existing method overload generation has an index-alignment bug when extension
method `this` stripping is active.

## Scope

- Old pipeline only (Transformers/ + ClassTransformer)
- Both constructors and methods
- Overload delegation pattern (matching the existing direction in `MethodTransformer`)

## Design

### 1. New file: `DefaultParameterHelper.cs`

Location: `src/CSharpToJava.Core/Transformers/Utilities/DefaultParameterHelper.cs`

A static helper class with a single public entry point that generates default-parameter
overloads for both methods and constructors.

```
public static List<JavaSyntaxNode> GenerateDefaultParameterOverloads(
    List<ParameterSyntax> allParams,
    IReadOnlyList<JavaParameter> javaParams,
    string memberName,
    string returnType,
    JavaModifiers modifiers,
    string leadingComment,
    List<JavaTypeParameter> typeParameters,
    bool isConstructor,
    bool isAbstract,
    bool hasStrippedThisParam,
    ConversionContext context,
    ExpressionTransformerFacade exprXf)
```

**Algorithm:**

1. Find `firstDefaultIdx` — first parameter where `Default != null`
2. Validate all trailing parameters from `firstDefaultIdx` onward have default values; skip if not
3. If abstract and not a constructor, skip entirely (abstract methods can't call concrete overloads)
4. For each `cutAt` from `firstDefaultIdx` to `paramCount - 1`:
   - Build overload: name, modifiers, returnType, type params, first `cutAt` params
   - Build call args: first `cutAt` use param names; remaining use transformed default values
   - Body: `this(args)` for constructors, `[return ]methodName(args)` for methods
5. Return `[fullDecl, overload1, ...]`

### 2. Modify `MethodTransformer.cs`

- Replace lines 265-306 (the inline overload generation) with a call to `DefaultParameterHelper`
- Fix extension method index alignment: pass `hasStrippedThisParam = true` so the helper
  adjusts indices when the `this` receiver was stripped from `javaParams`

### 3. Modify `ConstructorTransformer.cs`

- After parameter processing, call `DefaultParameterHelper.GenerateDefaultParameterOverloads(...)`
- When overloads are generated, return a `JavaMemberCollection` instead of a single
  `JavaConstructorDeclaration`
- Constructor overload bodies use `this(args)` to delegate to the full constructor

### 4. Modify `ClassTransformer.cs`

- Around line 1429, handle `JavaMemberCollection` as a return type from constructors
  (currently only handles `JavaConstructorDeclaration`), adding each member:
  - `JavaConstructorDeclaration` goes to `javaClass.Constructors`
  - `JavaMethodDeclaration` (constructor overload) goes to `javaClass.Constructors`

## Edge Cases

| Case | Handling |
|---|---|
| Abstract methods | Skip overload generation |
| Extension methods with defaults | `hasStrippedThisParam` accounts for index gap |
| Constructor with `this()`/`base()` init | Overload body uses `this(args)` to preserve the chain |
| Complex default expressions | Delegated to `ExpressionTransformerFacade` for conversion |
| Default value is null/default literal | Falls back to `"null"` string |
| Non-contiguous defaults (gaps) | Skip — only all-trailing-defaults pattern is supported |
| Extern/DllImport methods | Already handled in ClassTransformer after overload generation |

## Files Changed

| File | Change |
|---|---|
| `DefaultParameterHelper.cs` | **New** — shared overload generation |
| `MethodTransformer.cs` | Replace inline code with helper call |
| `ConstructorTransformer.cs` | Add overload generation via helper |
| `ClassTransformer.cs` | Handle `JavaMemberCollection` from constructors |

## Verification

- Unit test: constructor with single default param (e.g., `MyClass(int a, int b = 10)`)
- Unit test: constructor with multiple default params
- Unit test: method with default params via helper (existing behavior preserved)
- Unit test: abstract method with defaults (should skip)
- Integration: convert a C# class with constructor defaults, compile Java output
