# Foreach Variable Deconstruction Conversion Design

## Goal

Convert C# deconstruction foreach statements such as `foreach (var (p, q) in configurations)` to valid Java enhanced-for code without emitting `TODO: ForEachVariableStatement`.

## Design

Roslyn represents a deconstruction foreach variable as `ForEachVariableStatementSyntax`, separate from the existing `ForEachStatementSyntax` path. The transformer will add a dedicated branch that:

1. Transforms the collection expression and resolves its semantic element type.
2. Emits an enhanced-for loop over the mapped tuple element type (`Tuple2<...>`, `Tuple3<...>`, etc.).
3. Emits one local declaration per `SingleVariableDesignationSyntax` at the beginning of the loop body, using Vavr tuple accessors (`_1()`, `_2()`, ...).
4. Recursively handles nested parenthesized designations and preserves Java keyword escaping.
5. Reuses existing loop-body transformation, stream/materialization handling where applicable, and tuple type mapping/import registration.

Unsupported designation forms remain explicit TODOs rather than silently producing invalid identifiers.

## Testing

Add conversion tests for the reported named tuple case, a typed tuple case, and a nested designation. Assertions must verify successful conversion, absence of `TODO: ForEachVariableStatement`, mapped tuple type/accessors, and preserved loop-body references.
