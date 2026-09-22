# Closed Type Metadata Chain — Implementation Plan & Measured Results (2026-09-22)

Spec: `docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md`

## Goal

Root-fix the generated `dotnet-text-json` port failing on nested generic collection members
(`List<List<short>>` → `CSharpList<CSharpList<Integer>>` inner element converter wrongly
resolving to `StringConverter`, error "Cannot get the value of a token type 'StartArray' as a
string"). Member/element types must flow through the serializer metadata chain as closed-form
`java.lang.reflect.Type`, with converter resolution keyed by the closed type, and the chain
must not depend on any ThreadLocal registry.

## Landing configuration (ProjectPasses.cs switches)

| Switch | Landing state | Meaning |
|---|---|---|
| `ClosedTypeMetadataEnabled` | **true** | Closed chain patches (all `PatchClosed*`) |
| `WriteElementPropertyInfoEnabled` | **false** | Retired at landing (erased lookup overrode the closed answer) |
| `MemberClassInfoLookupEnabled` | **false** | Retired at landing (same statement rewritten) |
| `ElementArgumentScopeEnabled` | **false** | Retired (measured no-op; the closed rewrite owns the scope emission) |
| `MemberArgumentCaptureEnabled` | **false** | Retired (measured no-op; residue drained by `consumeMemberTypeArguments`) |

## Patches shipped (all behind `ClosedTypeMetadataEnabled`)

1. **PatchClosedMemberType** (JsonPropertyInfo.java) — stamps the member's declared closed
   `Type` (`TypeHelper.declaredGenericType`) after `setMemberInfo`; routes
   `getRuntimeClassInfo()` through `getOrAddClass(Type)`; erased lookup survives renamed
   (`__cs2jErasedRuntimeType`) so retired scope patches cannot match.
2. **PatchClosedParameterInfo** (JsonParameterInfo.java) — parameter mirror of (1); stamps
   from `matchingProperty.getDeclaredGenericType()`. Accessor injection is annotation-aware:
   lands **above** the `@CompatNonPublicProperty/@CompatNonPublicAccessor` block.
3. **PatchClosedElementClassInfo** (JsonClassInfo.java) — closed ctor records the closure,
   sets the erased element type, overlays the closed converter onto the class-info property,
   **primes the class-info property's `_runtimeClassInfo` with `this`** (see fix #3 below),
   and rebinds generic-definition members (Key/Value of `CSharpKeyValuePair<K,V>`) through
   `TypeHelper.substituteTypeVariables` including their bound constructor parameters; routes
   `getElementClassInfo()` through the closure's last type argument.
4. **PatchClosedOptionLookups** (JsonSerializerOptions.java) — `getConverter(Type)` and
   `getOrAddClass(Type)` overloads; caches keyed by `TypeHelper.closedCacheKey`
   (structural key; reflection `ParameterizedType` has no `equals`).
5. **PatchClosedMemberConverterResolution** (JsonSerializerOptions.java) —
   `determineConverter`'s default resolution goes through the member's declared closed type;
   attribute-resolved converters keep priority.
6. **PatchClosedFactoryEntry / PatchClosedEnumerableFactory / PatchClosedObjectFactory** —
   factories gain `createConverter(Class, Map<Type variables>, options)`; all inner
   `TypeHelper.getGenericArguments` calls rewired to
   `TypeHelper.genericArgumentsInLayers(layers, raw)`; legacy `createConverter(Class, options)`
   survives as a shim passing `null` layers.

Compat additions: `TypeHelper.collectClosureLayers/genericArgumentsInLayers/declaredGenericType/
closedCacheKey/substituteTypeVariables/createConverterForClosedType`,
`PropertyInfo.getGenericPropertyType`, `FieldInfo.getGenericType`,
`ConcurrentHashMapHelper.putIfAbsentUntyped`.

## Debugging history (each fixed test-first, unit tests pin the shape)

1. **Injection swallowed anchor signatures** — `Replace` dropped the anchor method signature — `Replace` dropped the anchor method signature
   (`getConverter(Class typeToConvert)` etc.) causing "statements outside a method" javac
   errors. Fixed by re-appending the anchor in the replacement; C# tests assert the anchor
   survives.
2. **Stamp landed in a static context** — the `setMemberInfo\(memberInfo\);` regex matched
   mid-line inside the static factory `createIgnoredPropertyPlaceholder`
   (`jsonPropertyInfo.setMemberInfo(memberInfo);`). Fixed with `Multiline` + `^` line-start
   anchor; the JsonPropertyInfo test snippet includes the static factory shape.
3. **Annotations sandwiched onto the injected field** — `getRuntimePropertyType()` in
   JsonParameterInfo is preceded by `@CompatNonPublicProperty/@CompatNonPublicAccessor`; the
   accessor injection landed between the annotations and the method (annotation target
   mismatch). Fixed with an annotation-aware anchor that inserts **above** the block; the
   JsonParameterInfo test snippet carries the annotated shape.
4. **KeyValuePair Key/Value deserialized as JsonElement** — the closed `JsonClassInfo` ctor
   built Key/Value properties through erased reflection (base ctor runs without any
   ThreadLocal warm-up), so bare type parameters (K/V) degraded to `Object`. Fixed with the
   rebind surgery: `TypeHelper.substituteTypeVariables` + `Class.getTypeParameters()` position
   mapping, rebinding property types, converters and bound constructor parameters. This fixed
   readNull 8/8 and the whole `readClassWithObject*` family.
5. **Nested writes still poisoned (write path)** — the write stack resolves element frames via
   `DeclaredJsonPropertyInfo` = the element class info's **class-info property**, whose lazy
   `getRuntimeClassInfo()` fell through to the erased class info (ThreadLocal residue) and
   baked `StringConverter` into nested element writes. First attempted priming with the
   element class info (NPE: object class infos return null `getElementClassInfo`), then
   `getOrAddClass(self)` inside the ctor (infinite recursion → StackOverflow across the
   suite). Final: prime with **`this`** — `__cs2jSetRuntimeClassInfo(this)`.
6. **determineConverter erased default** — the default member converter resolution ignored the
   closure; now closed-type aware (fix #5 in the patch list).

## Full-module regression & the six new failures (found and root-fixed)

Methodology: `mvn test -fae` on all 21 downstream modules; failing methods extracted from the
surefire failure listings as bare `Class.method` (positions and `-- Time elapsed` noise
stripped) and diffed with `comm` against the baseline capture.

| Configuration | F | E | unique failing methods |
|---|---|---|---|
| Baseline (ThreadLocal scope patches, closed chain off) | 386 | 470 | 314 |
| Closed chain, first landing | 384 | 286 | 289 (31 fixed, **6 new**) |
| Closed chain + fixes #7/#8 | 386 | 274 | 284 (32 fixed, 2 new) |
| Closed chain + fix #9 (drop-all consume) | 382 | 264 | 269 (**0 new vs baseline**, 45 fixed) |
| + Stage B retirement (`ElementArgumentScopeEnabled=false`) | 382 | 264 | 269 (identical set) |
| + Stage C retirement (`MemberArgumentCaptureEnabled=false`) | 382 | 264 | 269 (identical set) |

(Whole-reactor `-fae` numbers over 46 726 tests in 21 modules; the `dotnet-text-json-tests`
module alone goes 193F+235E → 191F+132E. Baseline TableRestore: 1F+31E → 1F+9E on the
82-case ArrayTests suite is unchanged by the retirement steps.)

The six new failures, all root-fixed test-first:

1. **readGenericIReadOnlyCollectionOfGenericIReadOnlyCollection /
   readGenericIReadOnlyListOfGenericIReadOnlyList (×2)** — `PatchClosedElementClassInfo`
   consumed the anchor statement that `PatchElementArgumentScope` needed
   (`_elementClassInfo = getOptions().getOrAddClass(getElementType());`), silently dropping
   the ThreadLocal scope from the generated file (field + capture landed at lines 102/307,
   lookup unwrapped; trace showed `[declared by a subtype]` erased resolution). **Fix #7
   (scope survival)**: the closed rewrite now re-emits the scope on its erased fallback —
   `pushArgumentContext` + `beginElementArgumentsScope` around the closed lookup, `finally`
   popping both — and owns the `__cs2jOwnerTypeArguments` capture + field;
   `PatchElementArgumentScope` gains an early-return guard when the closed rewrite is present.
2. **NullTests.defaultIgnoreNullValuesOnWrite (NPE)** — STJ's sealed `Write` wrapper
   null-guard (`value == null && !HandleNullOnWrite → WriteNullValue`) was dissolved in the
   port (`JsonConverter1.write` is abstract; only the slow path `tryWrite` kept it). The
   closed chain's *correct* dictionary value converter (`DateTimeConverter` instead of the
   erased null-tolerant `ObjectConverter`) surfaced the latent bug. **Fix #8
   (PatchFastPathWriteNullGuard)** — always-on (not gated): rewrites the 4 fast-path
   `elementConverter/valueConverter.write(...)` sites (ListOfTConverter, IListConverter,
   ArrayConverter, DictionaryOfTKeyTValueConverter) into an explicit null guard.
3. **ReferenceHandlerTests.dictionaryOfDictionaries** — same root cause and fix as (1).
4. **DictionaryTests.hashtable (ClassCast `String` → `JsonElement`) +
   iDictionary_Keys_ShouldBe_String_WhenDeserializing** — order-dependent: contaminated
   solely by `ArrayTests#readObjectArray`'s SimpleTestClass warm-up. DiagPoison (2-step
   repro: deserialize an object array, then a hashtable body, even with a *fresh* options)
   proved the poison is ThreadLocal-borne: `converterCacheKey(CSharpGenericIterable) =
   ... [member layer]` from step 1 leaked into step 2. Two earlier attempts did not cure it —
   draining the pending queue + recording a consumed layer (mirroring legacy consumption) and
   a conditional `TypeHelper.clearRegistrations()` at the three `deserialize` entries
   (`options == null` only, so the compat TypeReference flow that reuses caller options is
   untouched). **Fix #9 (drop-all consume)**: `TypeHelper.consumeMemberTypeArguments(closed)`
   now removes *all* per-raw bookkeeping — `TYPE_ARGUMENTS`, `CONSUMED_ARGUMENTS` and
   `MEMBER_ARGUMENTS` — for every raw class in the served closure's transitive closure. The
   closed chain never reads these layers, so dropping them cannot affect it; stale
   consumed/member replay, however, mis-keys later lookups on unrelated types sharing the raw
   class. Wired at all three closed member-creation sites (member default converter, member
   factory converter, closed class-info rebind loop). Trace after the fix: zero
   `[repeat of consumed layer]` / `[member layer]` lines in the poison repro, and step 2
   resolves `JsonElement` (was `String`).

Note on `hashtable`: the test casts dictionary values to `JsonElement`, i.e. the erased
resolution *is* the codified contract for that API shape — the drop-all restore of the
baseline behavior is the correct direction, not a workaround.

## Gates (final)

1. `dotnet test` (cs2j): **3185 passed / 0 failed** (26 in `ClosedTypeMetadataPatchTests`).
2. compat `mvn test`: **1722 passed / 0 failed**; installed to `D:\mvnrepo`.
3. Downstream: minimal repro (nested-List-only round-trip, mixed String/nested round-trip,
   deserialize-first order-independent — 3/3, temporary file deleted after acceptance) +
   full-module regression (see above).

## Measured results (downstream, `dotnet.text.json.Serialization.Tests.ArrayTests`, 82 tests)

| Configuration | Failures | Errors |
|---|---|---|
| Baseline (all ThreadLocal scope patches, closed chain off) | 1 | 31 |
| Closed chain landed | **1** | **9** |

Per-test diff (baseline vs closed):
- **New failures introduced: 0** (`comm -13` empty).
- **22 tests fixed**, including:
  - `readNull[2..8]` → **readNull 8/8** (includes the
    `TypeReference<CSharpList<SimpleTestClass>>` path at ArrayTests.java:245)
  - `readClassWithObject*` family (13 tests: ObjectArray/ICollection/ICollectionT/
    IEnumerable/IEnumerableT/IEnumerableConstructibleTypes/IList/IListT/
    IReadOnlyCollectionT/IReadOnlyListT/ImmutableTypes/List)
  - `readClassWithGenericISetT`, `readClassWithObjectISetT`
- Remaining 10 failures are a strict subset of the baseline failures
  (`write*Object*` family + `classWithNonNullEnumerableGettersIsParsed`); the full-data write
  now fails only on the **known unrelated** `ImmutableDictionary` ClassCast bug.
- `MyInt16TwoDimensionList` family: zeroed.

## Trace verification (`-Dcs2j.trace.typeargs=true`)

- Nested element resolution is fully closed:
  `genericArgumentsInLayers(CSharpList) = [CSharpList] → [CSharpList] → [Integer]`, all
  `[closed layer]`; no nested closure mis-resolves to `CSharpList[String]`.
- After fix #9 the poison repro (DiagPoison: deserialize an object array, then a hashtable
  body with fresh options) contains **zero** `[repeat of consumed layer]` / `[member layer]`
  lines and resolves the step-2 value converter to `JsonElement` (was poisoned to `String`).
- The readonly-nested TypeReference repro resolves the inner element through the closed
  rewrite's scope emission (`beginElementArgumentsScope`), with the entry itself on the
  session registry (allowed entry-path semantics, spec §2.4) — result `[[1, 2], [3, 4]]`.

## Retirement results (each step: flip switch → dotnet test → regenerate → full `-fae`)

- **Stage B** (`ElementArgumentScopeEnabled = false`): dotnet 3185/0; targeted 8/8;
  full run byte-identical to the pre-retirement capture (F=382 E=264, same 269 methods,
  `comm` diff empty both directions). The closed rewrite re-emits the scope wherever the
  retired patch would have — 退役的是作用域补丁，不是作用域本身.
- **Stage C** (`MemberArgumentCaptureEnabled = false`): same protocol, same outcome
  (F=382 E=264, identical set). `registerMemberType` stays for the entry/Class warm-up path.

## Gates

1. `dotnet test` (cs2j): **3176 passed / 0 failed** (17 in `ClosedTypeMetadataPatchTests`).
2. compat `mvn test`: **1719 passed / 0 failed**; installed to `D:\mvnrepo`.
3. Downstream: minimal repro (nested-List-only round-trip, mixed String/nested round-trip,
   deserialize-first order-independent — 3/3, temporary file deleted after acceptance) +
   full-module regression (see below).

## Retirement plan

Executed (results above): Stage B then Stage C, one at a time, each with a full `-fae`
regression — both measured no-ops. `registerMemberType` itself stays (Class/TypeReference
entry paths still warm it up).

## ClassCast root fix (2026-09-22, follow-up): immutable dictionaries realize IReadOnlyDictionary

The 35-of-36 ClassCast entries (`ImmutableDictionary`/`ImmutableSortedDictionary` cannot be
cast to `CSharpReadOnlyDict`, thrown at the converter's class-decl line) were not a
conversion bug: cs2j faithfully ports `where TCollection : IReadOnlyDictionary<TKey,TValue>`
as `TCollection extends CSharpReadOnlyDict<TKey,TValue>`, and the Java bridge method of the
untyped converter entry `checkcast`s to that bound (the stack's line number points at the
class declaration because that is where the bridge's LineNumberTable anchors). The defect
was in the compat runtime: `ImmutableDictionary`/`ImmutableSortedDictionary` implemented only
`IImmutableDictionary`, while .NET's real types realize `IReadOnlyDictionary` — so the
ported constraint was unsatisfiable and every immutable-dictionary value entering the
Object-typed converter entry threw.

Fix (compat-only, test-first `ImmutableDictionaryHierarchyTest`, 5 tests): both classes now
implement `CSharpReadOnlyDict<K,V>`. The blocker — `CSharpGenericIterable` (a
`Collection<T>`) declares `void clear()`, incompatible with the fluent `clear()` — was
resolved by removing fluent `clear()` from `IImmutableDictionary` and both classes
and adding `tryGetValue` to `ImmutableSortedDictionary`. compat 1727/0,
installed. Regenerated + full `-fae`: 269 → 262 failing methods, **7 fixed, 0 new**
(DictionaryTests arrayOfDictionary/dictionaryOfDictionary/dictionaryOfList/dictionaryOfString/
listOfDictionary/genericDictionary_WithObjectKeys_Throw_WhenDeserializing,
ExtensionDataTests.serializeIntoImmutableDictionaryProperty). The unblocked methods that
still fail now die deeper on **separate pre-existing bugs**: `EnumConverter.read` cannot
map UInt64-backed enums (SampleEnumUInt64, the biggest cluster — root-fixed in follow-up 2
below), nullable-element lists, and nested-dictionary creator instantiation.

## UInt64 enum root fix (2026-09-22, follow-up 2): unsigned-overload erasure collision

The SampleEnumUInt64 cluster traced to the WRITE side, not the reader: `EnumConverter`'s
ulong-backed write path calls `writer.WriteNumberValue(ulong)`, but the generated Java
bound it to `writeNumberValue(long)` — the SIGNED overload — so `ulong.MaxValue`
serialized as `-1`, which the (faithful) reader then rejects.

Root cause: `Utf8JsonWriter` declares same-width signed/unsigned overload pairs
(`WriteNumberValue(long)`/`(ulong)`, `(int)`/`(uint)`). After the unsigned→signed Java
type mapping both members erase to the same signature, but `JavaNaming.GetErasedTypeName`
compared C# full names (`System.UInt64` ≠ `System.Int64`) — no erasure conflict detected
→ no `_erasure_N` rename → the later (unsigned) declaration was silently dropped by the
duplicate-signature dedup, and call sites bound the signed one.

Fix (tests-first `UnsignedOverloadCollisionTests`):
1. `GetErasedTypeName` routes `INamedTypeSymbol` through `GetNamedErasedTypeName`,
   normalizing by Java width: UInt64→Int64, UInt32→Int32 (UInt16/Byte corrected in
   follow-up 3 below). The pairs are now conflicts; the unsigned overload survives as
   `_erasure_2` at declaration and call site.
2. Hand-written compat helpers must not receive erasure suffixes: `Utf8Formatter.TryFormat`/
   `Utf8Parser.TryParse` route to typed unsigned helpers (`tryFormatUInt64/32`,
   `tryParseUInt64/32/16`, `tryParseByte/SByte/Int16`) and a new `routedToCompatHelper`
   flag suppresses the call-site suffix for them.
3. compat `Utf8Formatter` gained `tryFormatUInt64`/`tryFormatUInt32`
   (`Long.toUnsignedString`/`Integer.toUnsignedString`).

Verified: standalone repro (`D:\tmp-diag\DiagEnum.java`) writes
`"MyUInt64Enum":18446744073709551615` and round-trips (was `-1` → `JsonException`).
compat 1728/0; full cs2j `dotnet test` 3191 passed / 0 failed.

## Correction (2026-09-22): `Clear()` was not dead API — static-helper routing

The ClassCast section's "verified dead API: no generated Java or C# source calls `Clear()`
on an immutable dictionary" claim was WRONG (a broken grep alternation swallowed the
matches; the xargs pipeline masked the failure). The follow-up-2 regeneration failed to
compile `GenericIImmutableDictionaryWrapper`/`StringToStringIImmutableDictionaryWrapper`:
`((IImmutableDictionary<TKey,TValue>)_dictionary).clear()` — the inherited
`Collection.clear()` is void, while C# `IImmutableDictionary.Clear()` RETURNS an empty
dictionary.

Fix:
- `IImmutableDictionary` gained a static helper `clear(dict)` returning an empty
  `ImmutableDictionary` (a fluent instance clear() still cannot exist on the interface).
- `InvocationExpressionTransformer` routes `IImmutableDictionary.Clear()` →
  `IImmutableDictionary.clear(receiver)`, guarded on `System.Collections.Immutable`
  (symbol-based, with a receiver-cast syntactic fallback for single-file conversions,
  which carry no `System.Collections.Immutable` reference — same pattern as the
  `Array.Clear` handler).
- `ImmutableDictionaryHierarchyTest` gained the static-helper test (compat 1728/0);
  new `ImmutableDictionaryClearRoutingTests` (2 tests, red→green) pins the routing and
  that mutable `List`/`Dictionary` `.Clear()` keep instance `clear()`.

## Erasure-width fix (2026-09-22, follow-up 3): Byte/UInt16 collide with Int32, not SByte/Int16

The full `-fae` after follow-up 2 surfaced `dotnet.xml.Tests.XmlConvertTests.runTests[196]`
(`ToTypeTests.toType33`: `ToString(ushort 4582)` → `"230"`). The Desktop baseline
`mvn clean package` (fail-fast) never reached the xml modules, so this was newly EXPOSED,
not a regression: before follow-up 2 this class generated duplicate `toString(int)`
declarations and could not compile at all.

The unit-test repro (8 integral overloads with distinctive bodies) exposed a miscompile in
my follow-up-2 normalization: it used C#-name widths — `Byte→System.SByte`,
`UInt16→System.Int16`. The transpiler's actual Java widths are `byte→int` and
`ushort→int` (masked `& 0xFF`/`& 0xFFFF` at use; sbyte→byte, short→short stay). So Byte
merged into the SByte group and UInt16 into the Int16 group: their declarations were
deduped away, and ushort/uint callers bound onto the byte-width overload.

Fix: `GetNamedErasedTypeName` normalizes `UInt16→System.Int32` and `Byte→System.Int32`
(UInt64→Int64, UInt32→Int32 unchanged). The int-width group {Int32, Byte, UInt16, UInt32}
now yields `toString`, `_erasure_2` (byte, `& 0xFF` body), `_erasure_3` (ushort,
`& 0xFFFF` body), `_erasure_4` (uint, `UnsignedMath.toStringUInt32` body); the
long/ulong group is unchanged. Regression test
`UnsignedOverloadCollisionTests.ByteUshortUintOverloads_AllSurviveUnderDistinctErasureSuffixes`
(red→green) pins the declarations, the body markers (no silent drops), and per-width
call-site binding.

Measured (identical extraction, `mvn -fae test` vs the Desktop pre-fix log): **277 → 254
failing methods, 0 new, 23 fixed overall** — 7 from the ClassCast fix, 14 from the
enum/erasure work (ArrayTests ×8 `writeClassWithObject*`/`writeObjectArray`/`write...ImmutableTypes`,
CacheTests `propertyCacheWithMinInputsFirst/Last`, ObjectTests
`readSimpleTestClassWithSimpleTestStruct`/`readSimpleTestStructWithSimpleTestClass`,
Utf8JsonWriterTests `writeNumber_NullPropertyName_UInt32/UInt64`), and
`XmlConvertTests.runTests` newly runnable (1924/1925 in that class). All 254 remaining
failures are pre-existing baseline entries in dotnet-text-json.

## Known unrelated bugs (must not degrade — verified unchanged)

`CSharpQueue`, `ISet` JsonElement ClassCast (note: `readClassWithGenericISetT` actually
improved to pass), `ImmutableArray` null; plus the follow-on bugs surfaced by the ClassCast
fix above (nullable-element list, nested-dictionary creator — the UInt64 enum conversion
was root-fixed in follow-up 2).
