# QueryExpressionTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Expression/Transformers/QueryExpressionTransformer.cs`

## 1. C# Language Feature Converted

`QueryExpressionTransformer` converts C# LINQ query expressions to Java Stream API chains. LINQ queries use SQL-like syntax:

```csharp
// C# query expression
var result = from x in source
             where x > 0
             orderby x descending
             select x * 2;
```

The transformer converts this to a Java `Stream` pipeline:
```java
var result = source.stream()
    .filter(x -> x > 0)
    .sorted(Comparator.reverseOrder())
    .map(x -> x * 2)
    .collect(Collectors.toList());
```

It handles:
- `from` clause (source/generator)
- `where` clause → `.filter()`
- `select` clause → `.map()` or `.collect()`
- `orderby` clause → `.sorted()`
- `group by` clause → `Collectors.groupingBy()`
- `let` clause (range variable) → `.map()` to introduce an alias pair
- Multiple `from` (cross-join) → `.flatMap()`
- `join` clause → (partially)
- `into` continuation

## 2. Deep Analysis of Current Code Issues

### Issue 1 — `join` clause emits `/* TODO */`

```csharp
from o in orders
join c in customers on o.CustomerId equals c.Id
select new { o.Total, c.Name }
```

The `join` clause handler emits `/* TODO: join */`. The Java Stream API does not have a native join operation; the common pattern is `flatMap` with a filter:
```java
orders.stream()
    .flatMap(o -> customers.stream()
        .filter(c -> o.customerId == c.id)
        .map(c -> ...))
```

### Issue 2 — `let` clause alias may not be visible to downstream transforms

```csharp
from x in source
let doubled = x * 2
where doubled > 0
select doubled
```

The `let` clause stores a range variable alias in a local dictionary. However, the `where` and `select` clauses that follow access `doubled` by looking up the identifier, which may not go through the same alias resolution path, causing `doubled` to be emitted as an undefined Java variable.

### Issue 3 — `group` lambda reuses the range variable after `map`

For group-by:
```csharp
from x in source
group x by x % 2
```

The transformer generates:
```java
source.stream()
    .collect(Collectors.groupingBy(x -> x % 2))
```

But if a preceding `.map()` introduced a new range variable, the group lambda still closes over `x` without accounting for the renamed variable from the map step. The `key selector` lambda should use the correct in-scope variable.

### Issue 4 — Array sources need `Arrays.stream()` not `.stream()`

```csharp
from x in new int[] { 1, 2, 3 }
select x * 2
```

When the source is an array type, the transformer calls `.stream()` on it. Java arrays do not have a `.stream()` method; the correct call is `Arrays.stream(arr)`. The transformer does not check the source type.

### Issue 5 — `into` continuation is not handled

```csharp
from x in source
group x by x % 2 into g
select new { Key = g.Key, Items = g.ToList() }
```

The `into` clause introduces a new range variable (`g`) scoped to the continuation of the query. The transformer does not implement `into`, so the generated Java stops at the `group` step and ignores the continuation.

### Issue 6 — `orderby` with multiple sort keys only applies the last one

```csharp
orderby x.LastName, x.FirstName
```

Multi-key `orderby` should produce a chained comparator:
```java
.sorted(Comparator.comparing(x -> x.lastName).thenComparing(x -> x.firstName))
```

The transformer only emits a single `sorted()` call using the first (or last) key.

## 3. Proposed Fixes

### Fix 1 — Implement `join` via `flatMap` + `filter`

```csharp
case JoinClauseSyntax join:
    return $".flatMap({outerVar} -> {inCollection}.stream()" +
           $".filter({innerVar} -> {TransformExpression(join.LeftExpression, context)} == " +
           $"{TransformExpression(join.RightExpression, context)})" +
           $".map({innerVar} -> /* result selector */)";
```

### Fix 2 — Track let variables in a dedicated scope map

```csharp
context.LetBindings[let.Identifier.Text] = $"{outerVar}.{let.Identifier.Text}";
// In downstream clauses, resolve identifiers through LetBindings before emitting
```

### Fix 3 — Update group key lambda to use the current range variable

```csharp
string currentVar = context.CurrentRangeVariable;  // track this through map steps
return $".collect(Collectors.groupingBy({currentVar} -> {TransformExpression(groupByKey, context)}))";
```

### Fix 4 — Detect array sources and use `Arrays.stream()`

```csharp
bool isArray = context.SemanticModel.GetTypeInfo(fromClause.Expression).Type is IArrayTypeSymbol;
string streamCall = isArray ? $"Arrays.stream({TransformExpression(fromClause.Expression, context)})"
                            : $"{TransformExpression(fromClause.Expression, context)}.stream()";
if (isArray) context.AddImport("java.util.Arrays");
```

### Fix 5 — Implement `into` by resetting the range variable scope

```csharp
case QueryContinuationSyntax cont:
    context.CurrentRangeVariable = cont.Identifier.Text;
    // Continue processing cont.Body as if it were a new query on the grouped result
```

### Fix 6 — Chain multiple `orderby` keys into a single `Comparator`

```csharp
string comparator = orderby.Orderings
    .Select((o, i) => i == 0
        ? $"Comparator.comparing({rangeVar} -> {TransformExpression(o.Expression, context)}){(o.Ascending ? "" : ".reversed()")}"
        : $".thenComparing({rangeVar} -> {TransformExpression(o.Expression, context)}){(o.Ascending ? "" : ".reversed()")}")
    .Aggregate((a, b) => $"{a}{b}");
```

## 4. Commit Changes

```
fix(query): implement join via flatMap+filter, fix let alias resolution, update group key to current range var, use Arrays.stream() for arrays, implement into continuation, and chain multi-key orderby comparators
```
