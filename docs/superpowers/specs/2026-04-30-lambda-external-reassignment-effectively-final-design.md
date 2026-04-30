# Lambda External Reassignment Effectively Final Fix

## Problem

Java requires lambda-captured local variables to be effectively final (no reassignment after declaration). The current `LambdaTransformer.GetMutatedCaptures` only detects mutations **inside** the lambda body, missing **external reassignments** that also violate effectively final.

```csharp
// C# (valid):
int x = 1;
Action a = () => Console.WriteLine(x);
x = 2; // external reassignment

// Current output (Java compile error):
int x = 1;
Runnable a = () -> System.out.println(x); // error: x not effectively final
x = 2;

// Expected output:
int x = 1;
int[] _x = { x };
Runnable a = () -> System.out.println(_x[0]);
_x[0] = 2;
```

## Root Cause

External reassignments may appear **before** or **after** the lambda. The current architecture processes statements sequentially; when the lambda is reached, prior statements are already generated and cannot be retroactively modified. A **pre-scan** phase is needed.

## Design

### Architecture: Pre-scan + Holder Mapping + Identifier Replacement

1. **Pre-scan** the method body before statement processing to identify all lambda-captured variables that are externally reassigned
2. **Register** pending holder mappings (varName → holderName) in `MethodConversionState`
3. **Create** holder declarations right after the variable declaration
4. **Activate** holder mapping after holder declaration, so `IdentifierExpressionTransformer` replaces all subsequent references

### Modified Files

#### 1. `MethodConversionState.cs` — Holder mapping state

Add pending/active holder mapping:
- `_pendingLambdaCaptureHolders`: registered during pre-scan, holder not yet declared
- `_activeLambdaCaptureHolders`: activated after variable declaration, triggers identifier replacement
- `LambdaCapturePreScanDone`: flag to prevent redundant pre-scans
- Methods: `RegisterPendingLambdaCaptureHolder`, `HasPendingLambdaCaptureHolder`, `ActivateLambdaCaptureHolder`, `TryGetActiveLambdaCaptureHolder`

#### 2. `StatementTransformer.cs` — Pre-scan entry

Call `PreScanLambdaCaptures` at method body level (ScopeDepth == 0) in `TransformBlock` and `TransformBlockToStructuredBody`.

#### 3. `StatementTransformer.cs` — `PreScanLambdaCaptures` method

Scan method body for all lambdas → collect captured ILocalSymbols → check if any are reassigned outside lambda spans → register pending holders.

#### 4. `StatementTransformer.Declarations.cs` — Holder creation

After variable declaration, check for pending holder → add holder declaration as post-statement → activate mapping.

#### 5. `IdentifierExpressionTransformer.cs` — Identifier replacement

Before existing identifier processing, check active holder mapping → return `_varName[0]` instead of `varName`.

#### 6. `LambdaTransformer.cs` — Dedup

Skip holder creation for variables already handled by pre-scan to avoid duplicate holders.

### Handled Scenarios

| Scenario | Example | Handling |
|----------|---------|----------|
| External post-assignment | `Action a=()=>x; x=2;` | Pre-scan + holder + identifier replacement |
| External pre-assignment | `x=2; Action a=()=>x;` | Pre-scan + holder + identifier replacement |
| Internal mutation (existing) | `Action a=()=>{x++;}` | Existing GetMutatedCaptures + regex |
| Both internal + external | `x=2; Action a=()=>{x++;}` | Pre-scan creates holder; GetMutatedCaptures skips duplicate |

### Not Handled (Future Iteration)

- **for-loop iteration variable capture**: `for (int i=0; i<10; i++) { Action a=()=>i; }` — the increment `i++` is external to the lambda but inside the for-statement. Pre-scan excludes for-initializer variables (pending holders can never be activated since `TransformLocalDeclaration` is not used for them), and `GetMutatedCaptures` only detects mutations inside the lambda body. The variable falls through both paths, producing Java code that violates the effectively-final constraint. This requires either: (a) emitting holder code in `TransformForStatement`, or (b) treating the entire for-loop body as a single analysis unit.

- **foreach iteration variable capture**: `foreach(var i in ls) Action a=()=>i;` — requires per-iteration holder, fundamentally different problem.
