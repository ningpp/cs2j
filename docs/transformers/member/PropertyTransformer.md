# PropertyTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs`

## 1. C# Language Feature Converted

`PropertyTransformer` converts C# property declarations to Java getter/setter methods with an optional backing field. C# properties encapsulate field access behind syntactic sugar:

```csharp
// C# auto-property
public string Name { get; set; }

// C# computed property
public int Area => Width * Height;

// C# property with validation in setter
public int Width {
    get => _width;
    set => _width = value > 0 ? value : throw new ArgumentException();
}
```

Java emits:
```java
// Auto-property
private String _name;
public String getName() { return _name; }
public void setName(String value) { this._name = value; }

// Computed
public int getArea() { return Width * Height; }

// With validation
private int _width;
public int getWidth() { return _width; }
public void setWidth(int value) { this._width = value > 0 ? value : ...; }
```

The transformer also handles static properties, abstract/virtual/override properties, `[Obsolete]` → `@Deprecated`, and XML doc comments.

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `isAutoProperty` detection logic is flawed by an OR condition

The code checks whether a property is "auto" (compiler-generated backing field, no custom body):
```csharp
bool isAutoProperty = getAccessor?.Body == null || setAccessor?.Body == null;
```

The logical OR (`||`) means the property is classified as auto if **either** accessor lacks a body — including properties with a custom getter and no setter, or vice versa. The correct condition is AND (`&&`): both accessors must have no body **and** both must be present for the property to be an auto-property. Get-only auto-properties (C# 6) have only a getter accessor with no body.

### Issue 2 — `init` accessor is not distinguished from `set`

C# 9 `init`-only setters allow mutation only during object initialization:
```csharp
public string Name { get; init; }
```

The transformer maps `init` to the same `setXxx` Java method as a regular `set` accessor. The generated setter is callable from anywhere, losing the compile-time restriction. A JavaDoc comment `/** @param value - set during construction only */` should be emitted, and where `--use-lombok` is enabled a `@Builder` annotation could be suggested.

### Issue 3 — `GetSingleAccessModifier` may fail on multi-modifier combinations

Some properties have mixed visibility:
```csharp
public string Name { get; protected set; }
```

Here the setter has a narrower visibility (`protected`) than the property itself (`public`). `GetSingleAccessModifier` attempts to extract a single modifier from the accessor's modifier token list. If the accessor has no explicit modifier (inheriting visibility from the property), it may return `null` or an unexpected value, resulting in a missing or incorrect access modifier on the setter method.

### Issue 4 — Property name to getter/setter name capitalization is not Unicode-safe

The rename is done with:
```csharp
string getterName = "get" + char.ToUpper(propertyName[0]) + propertyName[1..];
```

`char.ToUpper` is culture-sensitive and non-Unicode-safe. For identifiers that begin with a non-ASCII letter (which is valid in C#), `char.ToUpper` may produce incorrect results. `char.ToUpperInvariant` or `string.ToUpperInvariant` should be used.

### Issue 5 — Abstract/interface properties do not suppress the backing field

For abstract properties:
```csharp
public abstract string Name { get; }
```

No backing field should be emitted (there is no storage in the abstract class for this property). The transformer may emit a `private String _name;` backing field for abstract auto-properties, producing dead code in the Java abstract class.

### Issue 6 — Expression-body property getter uses deprecated `new ExpressionTransformer()`

Like `MethodTransformer`, some code paths inside `PropertyTransformer` still call `new ExpressionTransformer()` directly instead of going through `ExpressionTransformerFacade.Instance`.

## 3. Proposed Fixes

### Fix 1 — Fix `isAutoProperty` to use AND condition

```csharp
bool isAutoProperty = getAccessor?.Body == null
                   && getAccessor?.ExpressionBody == null
                   && (setAccessor == null
                       || (setAccessor.Body == null && setAccessor.ExpressionBody == null));
```

### Fix 2 — Detect `init` accessor and add JavaDoc comment

```csharp
bool isInitSetter = setAccessor?.IsKind(SyntaxKind.InitAccessorDeclaration) == true;
if (isInitSetter)
    setterMethod.Javadoc += " @implNote Set during construction only (C# init accessor).";
```

### Fix 3 — Handle missing accessor modifier by inheriting property visibility

```csharp
string accessorVisibility = accessor.Modifiers.Any()
    ? GetAccessModifier(accessor.Modifiers)
    : propertyVisibility;  // fall back to property-level visibility
```

### Fix 4 — Use `char.ToUpperInvariant` for identifier transformation

```csharp
string getterName = "get" + char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
```

### Fix 5 — Skip backing field for abstract/interface properties

```csharp
bool needsBackingField = isAutoProperty
    && !isAbstract
    && !context.IsInInterfaceBody;
if (needsBackingField)
    yield return EmitBackingField(propertyName, propertyType);
```

### Fix 6 — Replace `new ExpressionTransformer()` with `ExpressionTransformerFacade.Instance`

Search for all usages of `new ExpressionTransformer()` in `PropertyTransformer.cs` and replace with:
```csharp
ExpressionTransformerFacade.Instance.Transform(expr, context)
```

## 4. Commit Changes

```
fix(property): fix isAutoProperty OR→AND, distinguish init accessor, inherit accessor visibility, use ToUpperInvariant, omit backing field for abstract, and replace deprecated ExpressionTransformer usage
```
