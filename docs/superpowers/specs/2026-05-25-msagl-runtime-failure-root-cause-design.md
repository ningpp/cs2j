# MSAGL Runtime Failure Root-Cause Design

## Context

The converter project is `D:\code\cs2j`. The source C# project is
`E:\agl-master\GraphLayout\`, and generated Java is written under `E:\z5`.
The source C# project and generated Java output are read-only for this work.
All durable fixes must be made in the converter, compatibility runtime, or
conversion configuration in `D:\code\cs2j`.

The reported failures are runtime test failures after converting MSAGL from C#
to Java. They are not one failure. The logs show several independent symptom
families:

- Cluster and initial layout bounds assertions.
- Constrained Delaunay triangulation null edges.
- Reflection lookup of private static methods.
- MSTest resource and `TestContext`-driven graph loading.
- Stick constraint and overlap removal assertions.

The work will proceed one root cause at a time. Each root cause gets its own
minimal converter-side regression test, conversion re-run, targeted MSAGL
verification, and converter repository commit.

## Hard Constraints

- Do not modify `E:\agl-master\GraphLayout\`.
- Do not manually modify generated Java under `E:\z5`.
- Fix root causes in the converter instead of patching symptoms in generated
  output.
- Work one root cause at a time.
- After a root cause fix is verified by re-conversion, commit that fix in
  `D:\code\cs2j` before moving to the next root cause.
- Preserve unrelated user changes in the converter repository.

## Current Evidence

### Empty Rectangle Factory

`Rectangle(IEnumerable<Rectangle>)` cannot coexist in Java with
`Rectangle(IEnumerable<Point>)` as overloaded constructors because both erase to
`Rectangle(Iterable)`. The converter resolves the call site to
`Rectangle.createFrom_Iterable_Rectangle(...)`, but the generated factory body is
empty:

```java
public static Rectangle createFrom_Iterable_Rectangle(Iterable<Rectangle> rectangles) {
    Rectangle __inst = new Rectangle();
    return __inst;
}
```

The original C# constructor initializes an empty rectangle and adds each input
rectangle. The empty factory explains cluster and layout bounds failures because
cluster child bounds collapse to the default zero rectangle.

Primary affected failures:

- `ClusterTests.simpleDeepTranslationTest`
- `ClusterTests.nestedDeepTranslationTest`
- `InitialLayoutTests.denseClusteringNoEdges`
- Possibly later cluster and overlap-boundary failures.

### Reflection Method Lookup

The C# test uses:

```csharp
typeof(EdgeLabelPlacement).GetMethod(
    "GetPossibleSides",
    BindingFlags.Static | BindingFlags.NonPublic)
```

The generated Java currently calls:

```java
EdgeLabelPlacement.class.getDeclaredMethod("GetPossibleSides")
```

That loses parameter types, ignores Java naming conversion to
`getPossibleSides`, and does not model non-public lookup well enough. This
directly explains:

- `EdgeLabelPlacementTest.getPossibleSides`

### Cdt Null Edge

`CdtSweeper.insertSiteIntoFront` fails when `leftEdge` or `rightEdge` stays
`null`. The immediate null is not the root cause. The edge should already exist
in `pi.Edges`, so the investigation must trace site/edge construction,
orientation, ordering, and reference identity before any fix.

Primary affected failures:

- `CdtTests.smallTriangulation`
- `CdtTests.grid`
- `CdtTests.gridRotated`
- `CdtTests.alongFrontTest`
- `CdtTests.twoHoles`
- Routing and bundling tests that depend on CDT.

### MSTest Deployment And Graph Loading

Some failures load graph files and then call `createGeometryGraph()` on a null
drawing graph. A prior plan already identified the likely systemic causes:
MSBuild workspace resource copying, `[DeploymentItem]`, MSTest `TestContext`,
and multi-segment `Path.Combine`. These failures should be handled as a
separate root cause sequence rather than mixed with geometry algorithm fixes.

Primary affected failures:

- `IncrementalSugiyamaTests.nodeShapeChange`
- `MinimumWidthHeightTests.minimumSizeIsRespected`
- `SugiyamaEdgeLabelTests.labelsNearEdges`
- `SugiyamaLayoutTests.randomDotFileTests`

## Recommended Approach

Use a root-cause ledger and a strict red-green verification cycle for each
entry:

1. Reproduce or inspect the converted symptom enough to name the bad generated
   construct.
2. Add the smallest converter-side regression test that demonstrates the bad
   generated construct.
3. Run that test and confirm it fails for the expected reason.
4. Fix the converter or compatibility runtime.
5. Run the focused converter test and relevant existing converter tests.
6. Re-convert `E:\agl-master\GraphLayout\` to a clean output directory.
7. Run targeted Maven tests for the affected MSAGL failure family.
8. Commit the converter changes if the root cause is fixed.
9. Move to the next root cause only after the commit.

This approach favors reliable attribution over speed. It avoids hidden
dependency between fixes and leaves each commit with clear evidence.

## Initial Root-Cause Order

1. Fix the erased-constructor factory body for
   `Rectangle(IEnumerable<Rectangle>)`.
2. Fix C# reflection `Type.GetMethod` mapping for method name, binding flags,
   and parameter types.
3. Continue CDT investigation from edge creation and orientation semantics.
4. Apply the MSTest deployment/runtime plan if null graph-loading failures
   remain.
5. Reassess stick constraint and overlap failures after geometry bounds and CDT
   are fixed.

The order starts with the highest-confidence, broadest-impact root cause and
then moves toward failures requiring deeper algorithm tracing.

## Testing Design

Converter tests should live under `tests/CSharpToJava.Tests` and use existing
`ConversionPipeline` patterns.

For the rectangle factory fix, the regression test should convert a class or
struct with two `IEnumerable<T>` constructors whose erased Java signatures
conflict. It should assert that the generated static factory preserves the
original constructor body behavior, not just that the call site uses the factory
name.

For reflection, tests should cover:

- `typeof(T).GetMethod("PascalName", BindingFlags.Static | BindingFlags.NonPublic)`
  mapping to a usable Java lookup for `pascalName`.
- Parameter type propagation for methods with arguments.
- Generated imports or compatibility helpers needed by the mapping.

For CDT, no implementation test should be written until the original bad value
origin is found. The test should encode the smallest discovered C# construct
whose conversion changes edge/site semantics.

## End-to-End Verification

Use a fresh generated output for validation rather than editing generated Java
in place. The normal command shape is:

```powershell
.\scripts\convert-and-compile.ps1 `
  -SourcePath "E:\agl-master\GraphLayout\GraphLayout.sln" `
  -DestinationPath "E:\z5"
```

When a full Maven package is too broad for a single root-cause check, run
targeted Maven tests in the relevant generated module after conversion. The
final status for each root cause must state exactly which converter tests and
which MSAGL Java tests were run, including failures that remain outside the
fixed family.

## Commit Policy

Each verified root cause fix receives its own commit in `D:\code\cs2j`.
Commit messages should name the root cause, for example:

- `fix: preserve erased constructor factory bodies`
- `fix: map reflection GetMethod signatures`
- `fix: preserve CDT edge construction semantics`

Unrelated untracked or modified files should not be staged unless they are part
of the current root cause fix.
