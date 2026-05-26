# MSTest Deployment Runtime Fix Plan

## Problem

The converted MSAGL test project fails at runtime with missing deployment files such as:

```text
E:\z5\MSAGLTests\Out\Dots\fsm.dot
```

This is not a single missing file problem. It is a systemic mismatch between MSTest's runtime model and the Java/JUnit output produced by the converter.

The original C# test uses MSTest deployment semantics:

```csharp
[DeploymentItem("Resources/DotFiles/LevFiles", "Dots")]
public void RandomDotFileTests() {
    string fileName = GetDeploymentPath("Dots", "fsm.dot");
}
```

`GetDeploymentPath()` reads from `TestContext.DeploymentDirectory`, falling back to `TestRunDirectory/Out` only when no deployment directory exists. MSTest normally copies the declared deployment item into that deployment directory before the test runs.

The generated Java preserves the path lookup behavior but does not preserve the deployment behavior. Current `TestContext` also collapses `TestDir`, `DeploymentDirectory`, and `TestRunDirectory` to `user.dir`, which erases important MSTest semantics.

## Hard Constraints

- Do not modify the source C# project under `E:\agl-master\GraphLayout\`.
- Do not modify the generated Java project under `E:\z5`.
- Fix the converter and compatibility runtime so future conversions produce correct behavior.
- Avoid a narrow MSAGL-only patch. The fix should generalize to MSTest projects that use `TestContext`, `CopyToOutputDirectory`, and `DeploymentItem`.

## Root Causes

1. MSBuild workspace conversion does not carry non-source project resources.
   - `ProjectDiscovery.GetCopyResources()` reads `.csproj` `None` / `Content` items with `CopyToOutputDirectory`.
   - The ProjectGraph conversion path copies those resources.
   - The MSBuild Workspace path uses `WorkspaceProject`, which has no resource list, so copied test resources are lost.

2. `[DeploymentItem]` is ignored as a runtime contract.
   - Method/type attributes are used only for JUnit annotations such as `@Test`, `@BeforeEach`, and `@BeforeAll`.
   - No metadata or generated hook tells JUnit to copy deployment items to the expected target directory.

3. `TestContext` is too shallow.
   - `TestDir`, `DeploymentDirectory`, and `TestRunDirectory` are all `System.getProperty("user.dir")`.
   - This cannot model MSTest's separate run directory, deployment directory, and output directory.
   - Tests that ask for deployment files fall into synthetic paths that do not exist.

4. JUnit lifecycle conversion keeps incompatible `TestContext` parameters.
   - MSTest allows `ClassInitialize(TestContext)` and `AssemblyInitialize(TestContext)`.
   - Generated JUnit `@BeforeAll` methods keep the `TestContext` parameter.
   - JUnit 5 cannot inject that parameter without a custom `ParameterResolver`, causing `No ParameterResolver registered...`.

5. `Path.Combine(...)` varargs are incompletely lowered.
   - The two-argument case maps to `Paths.get(left, right).toString()`.
   - Multi-argument calls can be truncated, e.g. `Path.Combine(baseDirectory, "Resources", "MSAGLGeometryGraphs", filePath)` becoming only `Paths.get(baseDirectory, "Resources").toString()`.
   - This is not the direct `fsm.dot` failure, but it will break later resource resolution.

## Target Design

Implement a small MSTest compatibility runtime for JUnit instead of encoding test paths ad hoc in generated Java.

### Runtime Directory Model

`TestContext` should expose:

- `TestRunDirectory`: deterministic per-module test run root.
- `DeploymentDirectory`: directory where deployed test files are copied.
- `TestDir`: same meaning as MSTest's current test directory, usually the run/deployment directory for these converted tests.

Recommended default layout:

```text
<module working directory>
  target/
    cs2j-test-run/
      Out/
      Deployment/
```

The names can be simple and stable. They do not need to match Visual Studio's timestamped `TestResults` layout as long as `DeploymentDirectory`, `TestRunDirectory`, and `Out` semantics are consistent.

Allow overrides via system properties:

- `cs2j.testRunDirectory`
- `cs2j.deploymentDirectory`
- `cs2j.testDirectory`

### Deployment Metadata

Generated tests need a way to declare MSTest deployment items. Use generated annotations in the compat library:

```java
@MSTestDeploymentItem(source = "Resources/DotFiles/LevFiles", outputDirectory = "Dots")
```

For repeated attributes:

```java
@MSTestDeploymentItems({
    @MSTestDeploymentItem(source = "...", outputDirectory = "..."),
    @MSTestDeploymentItem(source = "...", outputDirectory = "...")
})
```

The annotation can be applied to classes and methods because MSTest supports both scopes.

### JUnit Extension

Add a JUnit 5 extension in the compat library, for example:

```java
@ExtendWith(MSTestExtension.class)
```

Responsibilities:

- Create a `TestContext`.
- Inject it into converted instance properties named like `TestContext` when present.
- Resolve method/class deployment annotations.
- Copy classpath or filesystem resources into `DeploymentDirectory`.
- Support `ParameterResolver` for lifecycle methods that still take `TestContext`.

This lets the converter either keep `ClassInitialize(TestContext)` parameters or later simplify them. The extension gives a compatibility safety net.

### Resource Copy Strategy

There are two independent resource flows:

1. Build-time resources from `.csproj`:
   - `CopyToOutputDirectory` files should be copied into Maven resources.
   - Test projects go to `src/test/resources`.
   - Production projects go to `src/main/resources`.

2. Runtime deployment from `[DeploymentItem]`:
   - Deployment source paths should resolve against classpath resources first.
   - If the file is not in classpath resources, resolve against the project/module working directory.
   - Directory deployment should copy recursively.
   - File deployment should copy to the target directory preserving file name.

This separation matters. `CopyToOutputDirectory` makes resources available to the Java build; `[DeploymentItem]` selects what appears in the MSTest-like deployment directory.

## Implementation Plan

### Phase 1: Preserve project resources in MSBuild workspace mode

Files:

- `src/CSharpToJava.Workspace/WorkspaceProject.cs`
- `src/CSharpToJava.Workspace/SolutionLoader.cs`
- `src/CSharpToJava.CLI/Program.cs`
- `src/CSharpToJava.CLI/ProjectDiscovery.cs`

Tasks:

- Add a resource item model to the workspace layer or move the existing CLI `ResourceItem` model to a shared place.
- Reuse `.csproj` XML scanning logic for both ProjectGraph and MSBuild Workspace paths.
- Populate `WorkspaceProject.ResourceItems`.
- Copy resources in `ConvertFromMSBuildWorkspaceMultiModule`, mirroring ProjectGraph behavior.
- Preserve resources through `FilterUnsupportedWorkspaceProjects`.
- Include resources in input fingerprinting so cache invalidates when resources change.

Tests:

- Unit test resource discovery for explicit files.
- Unit test wildcard resources such as `Resources\DotFiles\MoreGraphs\*.*`.
- Integration-style CLI test that a test project resource lands under `src/test/resources`.

Done when:

- A conversion through MSBuild Workspace emits `MSAGLTests/src/test/resources/Resources/DotFiles/LevFiles/fsm.dot`.
- The ProjectGraph resource path still works.

### Phase 2: Add MSTest deployment annotations and extension

Files:

- `java/csharptojava-compat/src/main/java/Microsoft/VisualStudio/TestTools/UnitTesting/TestContext.java`
- New compat files under `java/csharptojava-compat/src/main/java/Microsoft/VisualStudio/TestTools/UnitTesting/`
- `java/csharptojava-compat/pom.xml`

Tasks:

- Replace static string-only `TestContext` with an instance-capable context.
- Add deployment annotations.
- Add `MSTestExtension` implementing:
  - `BeforeAllCallback`
  - `BeforeEachCallback`
  - `ParameterResolver`
  - optionally `TestInstancePostProcessor`
- Copy deployment items before relevant tests.
- Add idempotency so the same class-level deployment is not recopied for every method.
- Ensure directories are created before use.

Tests:

- Compat unit test for default directory values.
- Compat unit test for system property overrides.
- Compat unit test for class-level directory deployment.
- Compat unit test for method-level file deployment.
- Compat unit test for `TestContext` parameter resolution.
- Compat unit test for instance property injection if practical.

Done when:

- A JUnit test using `@MSTestDeploymentItem(source = "Resources/DotFiles/LevFiles", outputDirectory = "Dots")` can read `context.getDeploymentDirectory()/Dots/fsm.dot`.
- `@BeforeAll static void init(TestContext context)` works.

### Phase 3: Convert `[DeploymentItem]` attributes

Files:

- `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`
- `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
- Supporting Java AST annotation rendering if needed.

Tasks:

- Parse `DeploymentItem` attribute arguments.
- Emit `@MSTestDeploymentItem` or container annotation on methods/classes.
- Add imports for deployment annotations.
- Add `@ExtendWith(MSTestExtension.class)` on classes that need MSTest runtime services:
  - uses `DeploymentItem`
  - has `TestContext` property
  - has lifecycle method with `TestContext` parameter
- Avoid adding extensions to non-test classes.

Tests:

- Converter test for `[DeploymentItem("Resources/DotFiles/LevFiles", "Dots")]`.
- Converter test for single-argument `[DeploymentItem("Resources/MSAGLGeometryGraphs")]`.
- Converter test for class-level deployment item.
- Converter test for multiple deployment items.

Done when:

- Generated Java contains deployment annotations and extension wiring without touching generated files manually.

### Phase 4: Fix MSTest `TestContext` lifecycle conversion

Files:

- `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
- Possibly class transformer/type post-processing for extension wiring.

Tasks:

- Keep `ClassInitialize(TestContext)` legal by relying on `MSTestExtension` `ParameterResolver`, or generate a wrapper that supplies `TestContext.current()`.
- Handle `AssemblyInitialize(TestContext)`.
- Ensure instance `TestContext` properties are initialized before `@BeforeEach`.
- Decide how to handle `TestContext.WriteLine` calls when no context is present.

Tests:

- Converter test for `[ClassInitialize] public static void Init(TestContext ctx)`.
- Converter test for `[AssemblyInitialize]`.
- Runtime compat test that the generated shape executes under JUnit.

Done when:

- JUnit no longer reports `No ParameterResolver registered for TestContext`.

### Phase 5: Fix `Path.Combine` varargs lowering

Files:

- `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- Tests around static type receiver resolution/path mapping.

Tasks:

- Map all `System.IO.Path.Combine` overloads to a helper that accepts varargs.
- Prefer `PathHelper.combine(...)` with `String... parts`, or generate `Paths.get(first, rest...).toString()` correctly.
- Add Java compat varargs overload if needed.

Tests:

- `Path.Combine(a, b)` keeps existing behavior.
- `Path.Combine(a, b, c)` emits all segments.
- `Path.Combine(a, b, c, d)` emits all segments.
- `Path.Combine(array)` if supported by the source mappings.

Done when:

- The MSAGL `ResolveTestFilePath` conversion preserves `"Resources", "MSAGLGeometryGraphs", filePath`.

### Phase 6: End-to-end MSAGL verification

Input:

```text
Source: E:\agl-master\GraphLayout\
Destination: fresh generated output, not E:\z5 unless explicitly chosen
```

Tasks:

- Run conversion with tests included.
- Verify generated `MSAGLTests/src/test/resources` contains:
  - `Resources/DotFiles/LevFiles/fsm.dot`
  - constraint data files
  - geometry graph files
- Run targeted tests first:
  - `SugiyamaLayoutTests#randomDotFileTests`
  - `IncrementalSugiyamaTests`
  - `MinimumWidthHeightTests`
  - `SugiyamaEdgeLabelTests`
  - `OverlapRemovalFileTests` class initialization
- Then run the broader module test suite if targeted tests pass.

Done when:

- No `FileNotFoundException` for `Out/Dots/*.dot`.
- No JUnit `ParameterResolutionException` for `TestContext`.
- No resource path truncation in `.geom` lookup paths.

## Acceptance Criteria

- No changes are required in the original C# MSAGL project.
- No manual edits are required in generated Java.
- Re-running conversion produces deployable test resources and MSTest-compatible JUnit wiring.
- `TestContext` models separate test run and deployment directories.
- `[DeploymentItem]` is represented in generated Java and executed at test runtime.
- Existing non-test conversions are not affected.
- Existing ProjectGraph resource copying remains compatible.

## Risk Areas

- Wildcard resource handling in legacy `.csproj` files can be subtle. Preserve relative paths exactly and add tests.
- Classpath directory enumeration is harder than direct filesystem copying. Prefer copying build resources into normal Maven resource directories, then deployment can resolve from classpath or module filesystem.
- JUnit extension ordering matters. Keep the extension small and deterministic.
- Per-method deployment items may need cleanup or overwriting between tests. Start with idempotent copy and stable directories; add cleanup only if tests require isolation.
- `TestContext` static fields may be tempting, but instance context plus `current()` fallback is safer for parallel tests.

## Suggested Implementation Order

1. Resource preservation in MSBuild workspace mode.
2. `TestContext` runtime and JUnit extension.
3. `[DeploymentItem]` conversion.
4. Lifecycle `TestContext` injection.
5. `Path.Combine` varargs.
6. MSAGL end-to-end verification.

This order fixes the data pipeline first, then the runtime semantics, then the generated syntax that connects them.
