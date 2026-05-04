# Fix Default Parameter Handling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate overload delegation pattern for C# methods and constructors with default parameter values in the old Transformer pipeline.

**Architecture:** A shared static helper `DefaultParameterHelper` encapsulates the overload generation algorithm. `MethodTransformer` and `ConstructorTransformer` call it after processing their full declaration. `ClassTransformer` handles `JavaMemberCollection` from both.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis), xUnit

---

## File Map

| File | Role |
|---|---|
| `src/CSharpToJava.Core/Transformers/Utilities/DefaultParameterHelper.cs` | **New** — shared overload generation logic |
| `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` | Replace inline overload code with helper call |
| `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs` | Add overload generation via helper |
| `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs` | Handle `JavaMemberCollection` from constructor transformer |
| `tests/CSharpToJava.Tests/DefaultParameterTests.cs` | **New** — unit tests |

---

### Task 1: Create DefaultParameterHelper

**Files:**
- Create: `src/CSharpToJava.Core/Transformers/Utilities/DefaultParameterHelper.cs`

- [ ] **Step 1: Create the Utilities directory**

```bash
mkdir -p src/CSharpToJava.Core/Transformers/Utilities
```

- [ ] **Step 2: Write DefaultParameterHelper.cs**

```csharp
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Utilities;

public static class DefaultParameterHelper
{
    /// <summary>
    /// Generates default-parameter overloads for a method.
    /// Returns overloads (full declaration excluded) or empty list.
    /// </summary>
    public static List<JavaSyntaxNode> GenerateMethodOverloads(
        List<ParameterSyntax> allParams,
        JavaMethodDeclaration fullMethod,
        bool isAbstract,
        bool hasStrippedThisParam,
        ConversionContext context,
        ExpressionTransformerFacade exprXf)
    {
        int firstDefaultIdx = allParams.FindIndex(p => p.Default != null);
        if (firstDefaultIdx < 0
            || !allParams.Skip(firstDefaultIdx).All(p => p.Default != null)
            || isAbstract)
        {
            return new List<JavaSyntaxNode>();
        }

        var overloads = new List<JavaSyntaxNode>();
        int javaParamOffset = hasStrippedThisParam ? 1 : 0;

        for (int cutAt = firstDefaultIdx; cutAt < allParams.Count; cutAt++)
        {
            var overload = new JavaMethodDeclaration
            {
                Name = fullMethod.Name,
                Modifiers = fullMethod.Modifiers,
                ReturnType = fullMethod.ReturnType,
                LeadingComment = fullMethod.LeadingComment,
            };
            foreach (var tp in fullMethod.TypeParameters)
                overload.TypeParameters.Add(tp);

            int javaCutAt = cutAt - javaParamOffset;
            for (int j = 0; j < javaCutAt && j < fullMethod.Parameters.Count; j++)
                overload.Parameters.Add(fullMethod.Parameters[j]);

            var callArgs = new List<string>();
            for (int i = 0; i < allParams.Count; i++)
            {
                if (i < cutAt)
                {
                    callArgs.Add(ConversionContext.EscapeJavaKeyword(allParams[i].Identifier.Text));
                }
                else
                {
                    var defaultVal = allParams[i].Default?.Value != null
                        ? exprXf.Transform(allParams[i].Default!.Value, context)
                        : "null";
                    callArgs.Add(defaultVal);
                }
            }

            string callPrefix = fullMethod.ReturnType == "void" ? "" : "return ";
            overload.Body = $"{callPrefix}{fullMethod.Name}({string.Join(", ", callArgs)});";
            overloads.Add(overload);
        }

        return overloads;
    }

    /// <summary>
    /// Generates default-parameter overloads for a constructor.
    /// Returns overloads (full declaration excluded) or empty list.
    /// </summary>
    public static List<JavaSyntaxNode> GenerateConstructorOverloads(
        List<ParameterSyntax> allParams,
        JavaConstructorDeclaration fullCtor,
        ConversionContext context,
        ExpressionTransformerFacade exprXf)
    {
        int firstDefaultIdx = allParams.FindIndex(p => p.Default != null);
        if (firstDefaultIdx < 0
            || !allParams.Skip(firstDefaultIdx).All(p => p.Default != null))
        {
            return new List<JavaSyntaxNode>();
        }

        var overloads = new List<JavaSyntaxNode>();

        for (int cutAt = firstDefaultIdx; cutAt < allParams.Count; cutAt++)
        {
            var overload = new JavaConstructorDeclaration
            {
                ClassName = fullCtor.ClassName,
                Modifiers = fullCtor.Modifiers,
                LeadingComment = fullCtor.LeadingComment,
            };

            for (int j = 0; j < cutAt && j < fullCtor.Parameters.Count; j++)
                overload.Parameters.Add(fullCtor.Parameters[j]);

            var callArgs = new List<string>();
            for (int i = 0; i < allParams.Count; i++)
            {
                if (i < cutAt)
                {
                    callArgs.Add(ConversionContext.EscapeJavaKeyword(allParams[i].Identifier.Text));
                }
                else
                {
                    var defaultVal = allParams[i].Default?.Value != null
                        ? exprXf.Transform(allParams[i].Default!.Value, context)
                        : "null";
                    callArgs.Add(defaultVal);
                }
            }

            overload.Body = $"this({string.Join(", ", callArgs)});";
            overloads.Add(overload);
        }

        return overloads;
    }
}
```

- [ ] **Step 3: Build to check for compilation errors**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Utilities/DefaultParameterHelper.cs
git commit -m "feat: add DefaultParameterHelper for shared overload generation

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 2: Modify MethodTransformer to use the helper

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs:265-306`

- [ ] **Step 1: Replace the inline overload generation with a helper call**

In `MethodTransformer.cs`, replace lines 265-306 (the block starting with `// Generate overloads for C# default parameters` through `return new JavaMemberCollection(overloads);`) with:

```csharp
        // Generate overloads for C# default parameters (Java doesn't support default parameter values)
        var allMethodParams = methodDecl.ParameterList?.Parameters.ToList() ?? new List<ParameterSyntax>();
        bool isAbstractMethod = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        var overloads = DefaultParameterHelper.GenerateMethodOverloads(
            allMethodParams,
            javaMethod,
            isAbstractMethod,
            hasStrippedThisParam: isExtensionMethod && context.Options.RewriteExtensionMethods,
            context,
            Transformers.Expression.ExpressionTransformerFacade.Instance);

        if (overloads.Count > 0)
        {
            var allDeclarations = new List<JavaSyntaxNode> { javaMethod };
            allDeclarations.AddRange(overloads);
            return new JavaMemberCollection(allDeclarations);
        }
```

(No using statement changes needed — `System.Linq` is used elsewhere in the file.)

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs
git commit -m "refactor: use DefaultParameterHelper in MethodTransformer

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 3: Modify ConstructorTransformer to generate overloads

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs`

- [ ] **Step 1: Add overload generation at end of Transform method**

In the `Transform` method, after the `IsProtectedInternal` block (before `return javaCtor;` at line 141), insert:

```csharp

        // Generate overloads for C# default parameters
        var allCtorParams = ctorDecl.ParameterList?.Parameters.ToList() ?? new List<ParameterSyntax>();
        var ctorOverloads = Utilities.DefaultParameterHelper.GenerateConstructorOverloads(
            allCtorParams,
            javaCtor,
            context,
            ExpressionTransformerFacade.Instance);

        if (ctorOverloads.Count > 0)
        {
            var allDeclarations = new List<JavaSyntaxNode> { javaCtor };
            allDeclarations.AddRange(ctorOverloads);
            return new JavaMemberCollection(allDeclarations);
        }

        return javaCtor;
```

Note: The existing `return javaCtor;` on line 141 should be replaced by the above block. The helper call goes right before it.

- [ ] **Step 2: Add using for Utilities namespace**

Add `using CSharpToJava.Core.Transformers.Utilities;` to the using statements at the top of `ConstructorTransformer.cs`.

- [ ] **Step 3: Build to verify compilation**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs
git commit -m "feat: generate default-parameter overloads for constructors

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 4: Modify ClassTransformer to handle JavaMemberCollection from constructors

**Files:**
- Modify: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs:1426-1437`

- [ ] **Step 1: Update constructor dispatch to handle JavaMemberCollection**

Replace lines 1426-1437:

```csharp
            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    AddCtorIfNotDuplicate(javaClass, javaCtor);
                }
                else if (ctor is JavaStaticInitializerBlock staticInitBlock)
                {
                    javaClass.StaticInitializers.Add(staticInitBlock);
                }
                break;
```

With:

```csharp
            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    AddCtorIfNotDuplicate(javaClass, javaCtor);
                }
                else if (ctor is JavaMemberCollection ctorCollection)
                {
                    foreach (var member in ctorCollection.Members)
                    {
                        if (member is JavaConstructorDeclaration ctorMember)
                            AddCtorIfNotDuplicate(javaClass, ctorMember);
                    }
                }
                else if (ctor is JavaStaticInitializerBlock staticInitBlock)
                {
                    javaClass.StaticInitializers.Add(staticInitBlock);
                }
                break;
```

- [ ] **Step 2: Build to verify compilation**

```bash
dotnet build src/CSharpToJava.Core/CSharpToJava.Core.csproj
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs
git commit -m "feat: handle JavaMemberCollection from constructor transformer

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 5: Write unit tests

**Files:**
- Create: `tests/CSharpToJava.Tests/DefaultParameterTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DefaultParameterTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void Constructor_OneDefaultParam_GeneratesOverload()
    {
        var result = Convert(@"
public class MyClass
{
    public MyClass(int a, int b = 10)
    {
    }
}
");

        Assert.True(result.Success);
        // Should contain a constructor with 1 param that delegates via this(a, 10)
        Assert.Contains("MyClass(int a)", result.GeneratedCode);
        Assert.Contains("this(a, 10);", result.GeneratedCode);
        // Should also contain the full constructor
        Assert.Contains("MyClass(int a, int b)", result.GeneratedCode);
    }

    [Fact]
    public void Constructor_MultipleDefaultParams_GeneratesMultipleOverloads()
    {
        var result = Convert(@"
public class Foo
{
    public Foo(string name = ""default"", int count = 0, bool flag = true)
    {
    }
}
");

        Assert.True(result.Success);
        // Full constructor: Foo(String name, int count, boolean flag)
        Assert.Contains("Foo(String name, int count, boolean flag)", result.GeneratedCode);
        // Overload 1: Foo() delegates via this("default", 0, true)
        Assert.Contains("Foo()", result.GeneratedCode);
        Assert.Contains("this(\"default\", 0, true);", result.GeneratedCode);
        // Overload 2: Foo(String name) delegates via this(name, 0, true)
        Assert.Contains("Foo(String name)", result.GeneratedCode);
        Assert.Contains("this(name, 0, true);", result.GeneratedCode);
        // Overload 3: Foo(String name, int count) delegates via this(name, count, true)
        Assert.Contains("Foo(String name, int count)", result.GeneratedCode);
        Assert.Contains("this(name, count, true);", result.GeneratedCode);
    }

    [Fact]
    public void Method_DefaultParams_GeneratesOverload()
    {
        var result = Convert(@"
public class Test
{
    public int Add(int x, int y = 5)
    {
        return x + y;
    }
}
");

        Assert.True(result.Success);
        // Full method: int add(int x, int y)
        Assert.Contains("int add(int x, int y)", result.GeneratedCode);
        // Overload: int add(int x) delegates via return add(x, 5);
        Assert.Contains("int add(int x)", result.GeneratedCode);
        Assert.Contains("return add(x, 5);", result.GeneratedCode);
    }

    [Fact]
    public void AbstractMethod_DefaultParams_Skipped()
    {
        var result = Convert(@"
public abstract class Base
{
    public abstract void Process(int x, int y = 10);
}
");

        Assert.True(result.Success);
        // Should have the abstract method but NO overload
        Assert.Contains("abstract void process(int x, int y)", result.GeneratedCode);
        // Should NOT generate a non-abstract overload that calls an abstract method
        int firstIdx = result.GeneratedCode.IndexOf("void process(int x, int y)");
        int lastIdx = result.GeneratedCode.LastIndexOf("void process(int x, int y)");
        Assert.Equal(firstIdx, lastIdx);
    }

    [Fact]
    public void VoidMethod_DefaultParam_NoReturnPrefix()
    {
        var result = Convert(@"
public class Logger
{
    public void Log(string message, string level = ""INFO"")
    {
    }
}
");

        Assert.True(result.Success);
        // Overload body: log(message, "INFO"); — NOT return log(...)
        Assert.Contains("log(", result.GeneratedCode);
        var overloadBody = "log(message, \"INFO\");";
        Assert.Contains(overloadBody, result.GeneratedCode);
    }

    [Fact]
    public void Constructor_NoDefaultParams_NoOverloads()
    {
        var result = Convert(@"
public class Simple
{
    public Simple(int a, int b)
    {
    }
}
");

        Assert.True(result.Success);
        // Only one constructor, no this() delegation
        Assert.Contains("Simple(int a, int b)", result.GeneratedCode);
        Assert.DoesNotContain("this(", result.GeneratedCode);
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail (feature not yet wired for constructors)**

```bash
dotnet test --filter "FullyQualifiedName~DefaultParameterTests"
```

Expected: Some tests pass (methods already work), constructor tests may fail.

- [ ] **Step 3: Run all tests to verify no regressions**

```bash
dotnet test
```

Expected: All existing tests pass, new constructor tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/CSharpToJava.Tests/DefaultParameterTests.cs
git commit -m "test: add default parameter handling tests

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
```

---

### Task 6: Final build and full test run

- [ ] **Step 1: Clean build**

```bash
dotnet build
```

Expected: Zero errors.

- [ ] **Step 2: Run full test suite**

```bash
dotnet test
```

Expected: All tests pass.

- [ ] **Step 3: Verify with a real CLI example (optional smoke test)**

```bash
echo 'public class Test { public Test(int a, int b = 10) {} }' > /tmp/TestDefault.cs
dotnet run --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert -i /tmp/TestDefault.cs
```

Expected: Output contains both `Test(int a, int b)` and `Test(int a)` with `this(a, 10);` body.
```
