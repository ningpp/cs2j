# Compat Reflection Gap Fill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the high-priority compat-library gaps surfaced by the `mvn clean package -e` log of `d:\cs-xml-20260716`, so the generated Java project advances past the current wave of "missing symbol" / "missing method" compilation errors.

**Architecture:** Keep all bridging helpers in the existing `io.github.ningpp.compat` package (`java/csharptojava-compat`). Extend `ReflectionHelper`, `TypeHelper`, `StringHelper`, `TypeInfo`, and `AssemblyCompat` for C# idioms that map onto Java reflection/APIs; add small marker/attribute compat classes where generated code references them. Update the C#→Java transformer to emit helper calls only where a direct Java equivalent does not exist. Each fix is validated by a red→green unit test before installation into the local Maven repo.

**Tech Stack:** C# (.NET Roslyn transformer), Java 21 compat library, Maven local repo at `D:\mvnrepo`.

---

## File Map

- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReflectionHelper.java`
  - Add `isPublic(MethodInfo)`, `isPublic(Constructor<?>)` overloads; keep existing `getBaseDefinition(Method)`, `isPublic(Method)`, `isOptional(Parameter)`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java`
  - Add `getIsGenericParameter(Class<?>)`, `makeArrayType(Class<?>)`, `getCustomAttributes(Class<?>, Class<?>, boolean)`, fix/adjust `getDefaultMembers(Class<?>)`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeInfo.java`
  - Add `getDeclaredMethod(String)`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java`
  - Add `append(StringBuilder, char, int)` mirroring `StringBuilder.Append(char, int)`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AssemblyCompat.java`
  - Add `getEntryAssembly()`, `loadFile(String)`, `loadWithPartialName(String)`, `getIsDynamic()`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultMemberAttribute.java` (new)
  - Marker annotation used by `TypeHelper.getDefaultMembers`.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FlagsAttribute.java` (new)
  - Marker annotation for `[Flags]` enums.
- `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeForwardedFromAttribute.java` (new)
  - Stub attribute referenced by generated `Compiler.java`.
- `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
  - Extend `System.Type` property bridge list; add `FieldInfo`/`MethodBase`/`ConstructorInfo` property bridges.
- `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
  - Add `StringBuilder.Append(char,int)` bridge; extend `Type.GetMethods`/`Type.GetConstructor` bridges.
- `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`
  - Regression tests for each bridged C# idiom.

---

## Error Analysis Summary (from `d:\cs-xml-20260716\mvn-build.log`)

Total: **287 errors**. Highest-frequency / easiest-to-close categories:

| Category | Count | Representative Missing Symbol | Fix Location |
|----------|-------|------------------------------|--------------|
| Reflection helper overloads | ~6 | `ReflectionHelper.isPublic(MethodInfo)`, `isPublic(Constructor)` | ReflectionHelper |
| `System.Type` → `Class` bridge | ~55 | `Class.getBaseType()`, `getIsGenericType()`, `getGenericArguments()`, `getDeclaringType()` | TypeHelper + transformer |
| `StringBuilder.Append(char,int)` | ~1 | `append(char,int)` | StringHelper + transformer |
| `TypeInfo` method gap | ~2 | `TypeInfo.getDeclaredMethod(String)` | TypeInfo |
| Assembly/Module bridge | ~8 | `AssemblyCompat.getEntryAssembly()`, `loadFile()`, `loadWithPartialName()`, `getIsDynamic()` | AssemblyCompat + transformer |
| Missing marker/attribute classes | ~4 | `FlagsAttribute`, `DefaultMemberAttribute`, `TypeForwardedFromAttribute` | new compat classes |
| `FieldInfo`/`MethodInfo` property type mapping | ~25 | Generated code uses `java.lang.reflect.Field` but calls `getFieldType()`/`getIsInitOnly()` | transformer type mapping |
| `ICustomAttributeProvider` casts | ~10 | `Class` / `java.lang.reflect.Field` → `ICustomAttributeProvider` | TypeHelper wrapper + transformer |
| Collection / enum / Decimal | ~130 | `CSharpICollection` casts, enum +/-, `int` ↔ `Decimal`, etc. | follow-up iterations after reflection gaps |

This plan targets the **reflection/string/assembly/marker gaps first** because they are (a) pure compat additions, (b) unblock the largest block of errors, and (c) do not require redesigning collection/enum lowering.

---

## Task 1: Baseline — install current compat and regenerate the XML project

**Files:**
- Test: generated project `d:\cs-xml-20260716`

- [ ] **Step 1: Install the current compat JAR**

  Run:
  ```powershell
  mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
  ```

  Expected: `BUILD SUCCESS`.

- [ ] **Step 2: Re-run the converter to refresh generated Java sources**

  Run:
  ```powershell
  dotnet run --project d:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj -- convert-project -s d:\csharpxml -d d:\cs-xml-20260716 --extra-deps io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT
  ```

  Expected: converter exits 0.

- [ ] **Step 3: Capture a fresh Maven log for the next iteration**

  Run:
  ```powershell
  cd d:\cs-xml-20260716
  mvn clean package -e *>&1 | Set-Content -Path d:\code\cs2j\mvn-build.log -Encoding UTF8
  ```

  Expected: log saved; we will use it to verify that each task removes its target errors.

---

## Task 2: Add missing `ReflectionHelper` overloads

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReflectionHelper.java`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

- [ ] **Step 1: Write the failing regression test**

  Add to `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`:

  ```csharp
  [Fact]
  public void ReflectionHelper_IsPublic_AcceptsMethodInfoAndConstructor()
  {
      var result = Convert("""
          using System;
          using System.Reflection;

          class Sample
          {
              bool Check(MethodInfo method, ConstructorInfo ctor)
              {
                  return method.IsPublic && ctor.IsPublic;
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("import io.github.ningpp.compat.ReflectionHelper;", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("ReflectionHelper.isPublic(method)", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("ReflectionHelper.isPublic(ctor)", result.GeneratedCode, StringComparison.Ordinal);
  }
  ```

  Run:
  ```powershell
  dotnet test --filter "FullyQualifiedName~ReflectionHelper_IsPublic_AcceptsMethodInfoAndConstructor"
  ```

  Expected: FAIL — generated code calls `.getIsPublic()` on raw `Method`/`Constructor`.

- [ ] **Step 2: Add overloads to `ReflectionHelper.java`**

  Append inside `ReflectionHelper`:

  ```java
  /**
   * Returns whether the given MethodInfo wrapper represents a public method.
   */
  public static boolean isPublic(MethodInfo method) {
      return method != null && isPublic(method.getMethod());
  }

  /**
   * Returns whether the given constructor is public.
   */
  public static boolean isPublic(Constructor<?> ctor) {
      return ctor != null && Modifier.isPublic(ctor.getModifiers());
  }
  ```

- [ ] **Step 3: Run the regression test**

  Run:
  ```powershell
  dotnet test --filter "FullyQualifiedName~ReflectionHelper_IsPublic_AcceptsMethodInfoAndConstructor"
  ```

  Expected: PASS.

- [ ] **Step 4: Install compat and verify the error count drops**

  Run:
  ```powershell
  mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
  cd d:\cs-xml-20260716
  mvn clean package -e *>&1 | Set-Content -Path d:\code\cs2j\mvn-build.log -Encoding UTF8
  ```

  Expected: `ReflectionHelper.isPublic(MethodInfo)` / `isPublic(Constructor)` errors are gone.

- [ ] **Step 5: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReflectionHelper.java
  git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs
  git commit -m "feat(compat): add isPublic overloads for MethodInfo and Constructor"
  ```

---

## Task 3: Bridge `StringBuilder.Append(char, int)`

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

- [ ] **Step 1: Write the failing regression test**

  Add:

  ```csharp
  [Fact]
  public void StringBuilder_AppendCharRepeatCount_BridgedToStringHelper()
  {
      var result = Convert("""
          using System.Text;

          class Sample
          {
              StringBuilder Append(StringBuilder sb, char c, int count)
              {
                  return sb.Append(c, count);
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("import io.github.ningpp.compat.StringHelper;", result.GeneratedCode, StringComparison.Ordinal);
      Assert.DoesNotContain(".append(c, count)", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("StringHelper.append(sb, c, count)", result.GeneratedCode, StringComparison.Ordinal);
  }
  ```

  Run and expect FAIL.

- [ ] **Step 2: Add helper to `StringHelper.java`**

  In the `StringBuilder helpers` region add:

  ```java
  /** Mirrors C# StringBuilder.Append(char, int) — appends a char repeatCount times. */
  public static StringBuilder append(StringBuilder builder, char c, int repeatCount) {
      if (builder == null) throw new NullPointerException("builder");
      if (repeatCount > 0) {
          for (int i = 0; i < repeatCount; i++) {
              builder.append(c);
          }
      }
      return builder;
  }
  ```

- [ ] **Step 3: Wire invocation in `InvocationExpressionTransformer.cs`**

  In `TransformMemberInvocation`, add after the existing StringBuilder special-cases:

  ```csharp
  // C# StringBuilder.Append(char, int) → StringHelper.append(StringBuilder, char, int)
  if (memberAccess.Name.Identifier.Text == "Append"
      && node.ArgumentList.Arguments.Count == 2
      && IsStringBuilderReceiver(memberAccess.Expression, context))
  {
      var arg0 = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
      var arg1 = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
      if (IsCharType(node.ArgumentList.Arguments[0].Expression, context)
          && IsIntegralType(node.ArgumentList.Arguments[1].Expression, context))
      {
          context.AddImport("io.github.ningpp.compat.StringHelper");
          var receiver = facade.Transform(memberAccess.Expression, context);
          return $"StringHelper.append({receiver}, {arg0}, {arg1})";
      }
  }
  ```

  Add helpers if absent:

  ```csharp
  private static bool IsStringBuilderReceiver(ExpressionSyntax expr, TransformationContext context)
  {
      var type = context.SemanticModel.GetTypeInfo(expr).Type;
      return type?.ToDisplayString() == "System.Text.StringBuilder";
  }

  private static bool IsCharType(ExpressionSyntax expr, TransformationContext context)
  {
      var type = context.SemanticModel.GetTypeInfo(expr).Type;
      return type?.SpecialType == SpecialType.System_Char;
  }

  private static bool IsIntegralType(ExpressionSyntax expr, TransformationContext context)
  {
      var type = context.SemanticModel.GetTypeInfo(expr).Type;
      return type is { SpecialType: SpecialType.System_Int32 or SpecialType.System_Int64 };
  }
  ```

- [ ] **Step 4: Run the regression test**

  Expected: PASS.

- [ ] **Step 5: Install compat and rerun generated project build**

  Same Maven commands as Task 2 Step 4.

- [ ] **Step 6: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java
  git add src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs
  git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs
  git commit -m "feat(compat): bridge StringBuilder.Append(char, int) via StringHelper"
  ```

---

## Task 4: Extend `TypeHelper` for remaining `System.Type` idioms

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultMemberAttribute.java`
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

- [ ] **Step 1: Write failing regression tests**

  Add:

  ```csharp
  [Fact]
  public void SystemType_MakeArrayType_And_GenericParameter_BridgedToTypeHelper()
  {
      var result = Convert("""
          using System;

          class Sample
          {
              bool Check(Type type)
              {
                  return type.IsGenericParameter && type.MakeArrayType() != null;
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("TypeHelper.getIsGenericParameter(type)", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("TypeHelper.makeArrayType(type)", result.GeneratedCode, StringComparison.Ordinal);
  }

  [Fact]
  public void SystemType_CustomAttributes_BridgedToTypeHelper()
  {
      var result = Convert("""
          using System;
          using System.Reflection;

          class Sample
          {
              object[] Check(Type type)
              {
                  return type.GetCustomAttributes(typeof(ObsoleteAttribute), false);
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("TypeHelper.getCustomAttributes(type", result.GeneratedCode, StringComparison.Ordinal);
  }
  ```

  Run and expect FAIL.

- [ ] **Step 2: Create `DefaultMemberAttribute.java`**

  Create `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultMemberAttribute.java`:

  ```java
  package io.github.ningpp.compat;

  import java.lang.annotation.ElementType;
  import java.lang.annotation.Retention;
  import java.lang.annotation.RetentionPolicy;
  import java.lang.annotation.Target;

  /**
   * Marker annotation mirroring C# System.Reflection.DefaultMemberAttribute.
   */
  @Retention(RetentionPolicy.RUNTIME)
  @Target({ElementType.TYPE})
  public @interface DefaultMemberAttribute {
      String value();
  }
  ```

- [ ] **Step 3: Add methods to `TypeHelper.java`**

  Add after `getArrayRank`:

  ```java
  public static boolean getIsGenericParameter(Class<?> type) {
      return type != null && type.isPrimitive() == false && java.lang.reflect.TypeVariable.class.isInstance(type);
  }

  public static Class<?> makeArrayType(Class<?> elementType) {
      if (elementType == null) {
          return null;
      }
      return java.lang.reflect.Array.newInstance(elementType, 0).getClass();
  }

  public static Object[] getCustomAttributes(Class<?> type, Class<?> attributeType, boolean inherit) {
      if (type == null || attributeType == null) {
          return new Object[0];
      }
      return type.getAnnotationsByType((Class) attributeType);
  }

  public static Object[] getCustomAttributes(Class<?> type, boolean inherit) {
      if (type == null) {
          return new Object[0];
      }
      return type.getAnnotations();
  }
  ```

  Fix `getDefaultMembers` to use `DefaultMemberAttribute` and return `MemberInfo` concrete subclasses:

  ```java
  public static MemberInfo[] getDefaultMembers(Class<?> type) {
      if (type == null) {
          return new MemberInfo[0];
      }
      List<MemberInfo> members = new ArrayList<>();
      DefaultMemberAttribute attr = type.getAnnotation(DefaultMemberAttribute.class);
      if (attr == null) {
          return members.toArray(new MemberInfo[0]);
      }
      String name = attr.value();
      for (Method method : type.getMethods()) {
          if (method.getName().equals(name)) {
              members.add(new MethodInfo(method));
          }
      }
      for (Field field : type.getFields()) {
          if (field.getName().equals(name)) {
              members.add(new FieldInfo(field));
          }
      }
      return members.toArray(new MemberInfo[0]);
  }
  ```

- [ ] **Step 4: Wire property getters in `IdentifierExpressionTransformer.cs`**

  Extend the existing `System.Type` special-case to include the new properties:

  ```csharp
  // C# System.Type properties mapped to java.lang.Class need TypeHelper bridges.
  if (IsSystemType(receiverType) && memberName is "BaseType" or "DeclaringType" or "IsGenericType"
      or "ContainsGenericParameters" or "IsAbstract" or "IsValueType" or "IsVisible"
      or "IsNestedPublic" or "IsClass" or "ArrayRank" or "IsGenericParameter")
  {
      context.AddImport("io.github.ningpp.compat.TypeHelper");
      return $"TypeHelper.get{memberName}({target})";
  }

  if (IsSystemType(receiverType) && memberName == "GenericArguments")
  {
      context.AddImport("io.github.ningpp.compat.TypeHelper");
      return $"TypeHelper.getGenericArguments({target})";
  }

  if (IsSystemType(receiverType) && memberName == "MakeArrayType")
  {
      context.AddImport("io.github.ningpp.compat.TypeHelper");
      return $"TypeHelper.makeArrayType({target})";
  }
  ```

  (The invocation form `type.MakeArrayType()` will be handled by the existing member-invocation path; ensure the method exists in TypeHelper.)

- [ ] **Step 5: Run regression tests**

  Expected: PASS.

- [ ] **Step 6: Install compat and rerun generated project build**

  Same Maven commands.

- [ ] **Step 7: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DefaultMemberAttribute.java
  git add src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
  git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs
  git commit -m "feat(compat): extend TypeHelper with MakeArrayType, IsGenericParameter, GetCustomAttributes and DefaultMemberAttribute"
  ```

---

## Task 5: Add missing marker/attribute compat classes

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FlagsAttribute.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeForwardedFromAttribute.java`

- [ ] **Step 1: Create `FlagsAttribute.java`**

  ```java
  package io.github.ningpp.compat;

  import java.lang.annotation.ElementType;
  import java.lang.annotation.Retention;
  import java.lang.annotation.RetentionPolicy;
  import java.lang.annotation.Target;

  /**
   * Marker annotation mirroring C# System.FlagsAttribute.
   */
  @Retention(RetentionPolicy.RUNTIME)
  @Target({ElementType.TYPE})
  public @interface FlagsAttribute {
  }
  ```

- [ ] **Step 2: Create `TypeForwardedFromAttribute.java`**

  ```java
  package io.github.ningpp.compat;

  import java.lang.annotation.ElementType;
  import java.lang.annotation.Retention;
  import java.lang.annotation.RetentionPolicy;
  import java.lang.annotation.Target;

  /**
   * Stub annotation mirroring C# System.Runtime.CompilerServices.TypeForwardedFromAttribute.
   */
  @Retention(RetentionPolicy.RUNTIME)
  @Target({ElementType.TYPE})
  public @interface TypeForwardedFromAttribute {
      String value();
  }
  ```

- [ ] **Step 3: Install compat and verify**

  Run:
  ```powershell
  mvn clean install -DskipTests -f d:\code\cs2j\java\csharptojava-compat\pom.xml
  cd d:\cs-xml-20260716
  mvn clean package -e *>&1 | Set-Content -Path d:\code\cs2j\mvn-build.log -Encoding UTF8
  ```

  Expected: `FlagsAttribute` / `TypeForwardedFromAttribute` cannot-find-symbol errors are gone.

- [ ] **Step 4: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/FlagsAttribute.java
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeForwardedFromAttribute.java
  git commit -m "feat(compat): add FlagsAttribute and TypeForwardedFromAttribute stubs"
  ```

---

## Task 6: Extend `AssemblyCompat` for XML serializer assembly operations

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AssemblyCompat.java`

- [ ] **Step 1: Add missing assembly helpers**

  Append to `AssemblyCompat`:

  ```java
  /** Mirrors C# Assembly.GetEntryAssembly() */
  public static AssemblyCompat getEntryAssembly() {
      return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), "entry");
  }

  /** Mirrors C# Assembly.LoadFile(path) */
  public static AssemblyCompat loadFile(String path) {
      return loadFrom(path);
  }

  /** Mirrors C# Assembly.LoadWithPartialName(name) */
  public static AssemblyCompat loadWithPartialName(String name) {
      return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), name);
  }

  /** Mirrors C# Assembly.IsDynamic */
  public boolean getIsDynamic() {
      return false;
  }
  ```

- [ ] **Step 2: Install compat and verify**

  Same Maven commands as previous tasks.

  Expected: `getEntryAssembly()`, `loadFile()`, `loadWithPartialName()`, `getIsDynamic()` errors are gone.

- [ ] **Step 3: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/AssemblyCompat.java
  git commit -m "feat(compat): add AssemblyCompat entry/loadFile/loadWithPartialName/isDynamic stubs"
  ```

---

## Task 7: Add `TypeInfo.getDeclaredMethod(String)`

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeInfo.java`

- [ ] **Step 1: Add the method**

  Append to `TypeInfo`:

  ```java
  public MethodInfo getDeclaredMethod(String name) {
      if (name == null || type == null) {
          return null;
      }
      for (java.lang.reflect.Method method : type.getDeclaredMethods()) {
          if (method.getName().equals(name)) {
              return new MethodInfo(method);
          }
      }
      return null;
  }
  ```

- [ ] **Step 2: Install compat and verify**

  Same Maven commands.

  Expected: `TypeInfo.getDeclaredMethod(String)` errors are gone.

- [ ] **Step 3: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeInfo.java
  git commit -m "feat(compat): add TypeInfo.getDeclaredMethod(String)"
  ```

---

## Task 8: Bridge `FieldInfo`/`MethodInfo`/`ConstructorInfo` property getters in the transformer

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- Test: `tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs`

- [ ] **Step 1: Write failing regression tests**

  Add:

  ```csharp
  [Fact]
  public void ReflectionFieldInfo_FieldType_IsInitOnly_IsStatic_Bridged()
  {
      var result = Convert("""
          using System.Reflection;

          class Sample
          {
              bool Check(FieldInfo field)
              {
                  return field.FieldType != null && field.IsInitOnly && field.IsStatic;
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("import io.github.ningpp.compat.ReflectionHelper;", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("ReflectionHelper.getFieldType(field)", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("ReflectionHelper.isInitOnly(field)", result.GeneratedCode, StringComparison.Ordinal);
      Assert.Contains("ReflectionHelper.isStatic(field)", result.GeneratedCode, StringComparison.Ordinal);
  }

  [Fact]
  public void ReflectionMethodBase_IsStatic_Bridged()
  {
      var result = Convert("""
          using System.Reflection;

          class Sample
          {
              bool Check(MethodInfo method)
              {
                  return method.IsStatic;
              }
          }
          """);

      Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
      Assert.Contains("ReflectionHelper.isStatic(method)", result.GeneratedCode, StringComparison.Ordinal);
  }
  ```

  Run and expect FAIL.

- [ ] **Step 2: Add property helpers to `ReflectionHelper.java`**

  Add if not already present:

  ```java
  public static Class<?> getFieldType(Field field) {
      return field == null ? null : field.getType();
  }

  public static boolean isInitOnly(Field field) {
      return field != null && Modifier.isFinal(field.getModifiers());
  }

  public static boolean isStatic(Field field) {
      return field != null && Modifier.isStatic(field.getModifiers());
  }

  public static boolean isStatic(Method method) {
      return method != null && Modifier.isStatic(method.getModifiers());
  }
  ```

- [ ] **Step 3: Wire property getters in `IdentifierExpressionTransformer.cs`**

  Add near the existing reflection property special-cases:

  ```csharp
  // C# FieldInfo.FieldType / IsInitOnly / IsStatic on raw java.lang.reflect.Field
  if (IsJavaReflectField(receiverType))
  {
      context.AddImport("io.github.ningpp.compat.ReflectionHelper");
      if (memberName == "FieldType") return $"ReflectionHelper.getFieldType({target})";
      if (memberName == "IsInitOnly") return $"ReflectionHelper.isInitOnly({target})";
      if (memberName == "IsStatic") return $"ReflectionHelper.isStatic({target})";
  }

  // C# MethodBase.IsStatic on raw java.lang.reflect.Method
  if (IsJavaReflectMethod(receiverType) && memberName == "IsStatic")
  {
      context.AddImport("io.github.ningpp.compat.ReflectionHelper");
      return $"ReflectionHelper.isStatic({target})";
  }
  ```

  Add helpers:

  ```csharp
  private static bool IsJavaReflectField(ITypeSymbol? type)
  {
      return type?.ToDisplayString() == "System.Reflection.FieldInfo"
          || type?.ToDisplayString() == "java.lang.reflect.Field";
  }

  private static bool IsJavaReflectMethod(ITypeSymbol? type)
  {
      return type?.ToDisplayString() == "System.Reflection.MethodInfo"
          || type?.ToDisplayString() == "System.Reflection.MethodBase"
          || type?.ToDisplayString() == "java.lang.reflect.Method";
  }
  ```

- [ ] **Step 4: Run regression tests**

  Expected: PASS.

- [ ] **Step 5: Install compat, regenerate, and rebuild**

  Run converter + Maven commands as in Task 1.

  Expected: field/method `IsStatic`/`FieldType`/`IsInitOnly` errors drop.

- [ ] **Step 6: Commit**

  ```bash
  git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReflectionHelper.java
  git add src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs
  git add tests/CSharpToJava.Tests/CSharpXmlCompileRegressionTests.cs
  git commit -m "feat(compat): bridge FieldInfo/MethodBase properties on raw Java reflection types"
  ```

---

## Task 9: Continue iterating over the next first error

- [ ] **Step 1: Re-run `mvn clean package -e` and save the log**

  ```powershell
  cd d:\cs-xml-20260716
  mvn clean package -e *>&1 | Set-Content -Path d:\code\cs2j\mvn-build.log -Encoding UTF8
  ```

- [ ] **Step 2: Extract the new first error**

  Read `d:\code\cs2j\mvn-build.log` and identify the first remaining `[ERROR]` line.

- [ ] **Step 3: Add the next regression test and fix**

  Follow the same Red→Green pattern for the next missing compat class/method or transformer bridge.

- [ ] **Step 4: Continue until `BUILD SUCCESS`**

  Repeat Tasks 1–9 until `mvn clean package -e` exits with `BUILD SUCCESS`.

---

## Spec Coverage / Self-Review

1. **Reflection helper overloads:** Task 2 covers `isPublic(MethodInfo)` and `isPublic(Constructor)`.
2. **StringBuilder gap:** Task 3 covers `Append(char, int)`.
3. **System.Type → Class gaps:** Task 4 extends `TypeHelper` with `MakeArrayType`, `IsGenericParameter`, `GetCustomAttributes`, and `DefaultMemberAttribute`.
4. **Marker/attribute stubs:** Task 5 covers `FlagsAttribute` and `TypeForwardedFromAttribute`.
5. **Assembly operations:** Task 6 covers `getEntryAssembly`, `loadFile`, `loadWithPartialName`, `getIsDynamic`.
6. **TypeInfo gap:** Task 7 covers `getDeclaredMethod(String)`.
7. **Field/Method property bridges:** Task 8 covers `FieldType`, `IsInitOnly`, `IsStatic` on raw reflection types.
8. **Iteration loop:** Task 9 formalizes the repeated log-analysis → test → fix cycle.

**Placeholder scan:** All steps contain concrete code/commands; no TBD/TODO/fill-in-later.

**Type consistency:** Helper method names (`getBaseType`, `isPublic`, `getFieldType`) match between Java helpers and C# transformer emission strings.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-07-20-compat-reflection-gap-fill.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using `executing-plans`, batch execution with checkpoints.

**Which approach?**
