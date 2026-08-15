# Parallel Agent Dispatch Plan (2026-08-15)

Shared context: converter repo `D:\code\cs2j`; C# source `D:\csharp-sdk` READ-ONLY;
generated output dirs `D:\cs-java-81319-N` per agent; latest failure log
`D:\cs-java-81512\mvn-build4.log`; surefire reports under
`D:\cs-java-81512\system-text-json-tests\target\surefire-reports\`.

Constraints for every agent:
- Fix only `D:\code\cs2j` (converter `src/` or compat `java/csharptojava-compat/`).
- Never edit `D:\csharp-sdk` or generated Java dirs except own `D:\cs-java-81319-N`.
- 300s timeout for every dotnet/mvn command.
- dotnet build/test serialized via `D:\code\cs2j\.dotnet-build.lock` exclusive file.
- Append analysis to `sdk-error-analysis.md`; commit only own changed paths.
- Do not run whole `system-text-json-tests` without excluding `Utf8JsonWriterTests`
  (`-Dtest='!dotnet.text.json.Tests.Utf8JsonWriterTests'`).

## Running agents (max 4 active slots including root)

1. `/root/agent1_writer_loop` — Utf8JsonWriterTests infinite loop (assumed).
2. `/root/agent2_json_document` — JsonDocument/JsonElement DOM cluster (assumed).
3. `/root/agent3_json_reader` — Utf8JsonReader/reader-semantics cluster (assumed).

## Pending dispatch queue (spawn as slots free, N = agent number)

### Agent 4 — compat interface/array/type-detection cluster
- Evidence: `ClassCastException: [Ljava.lang.Integer; cannot be cast to class io.github.ningpp.compat.CSharpGenericIterable`;
  `UnsupportedOperationException: Deserialization of interface types is not supported. Type 'interface io.github.ningpp.compat.CSharpGenericIDictionary'`;
  NPE `"converter" is null` at `JsonConverterFactory.getConverterInternal`;
  `JsonException: could not be converted to class io.github.ningpp.compat.ImmutableList/ImmutableArray/CSharpDictionary`.
- Files: `D:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\Serialization\Converters\IEnumerableConverterFactory.java`,
  `.../IEnumerableConverterFactoryHelpers.java`, `.../JsonConverterFactory.java`,
  `D:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonSerializerOptions.java`,
  compat `TypeHelper.java`, `CSharpGenericIterable.java`, `CSharpGenericIDictionary.java`, `CSharpDictionary.java`, `Immutable*.java`.
- Also fold in nullable-bool conditional NPE:
  `(stackType != null ? stackType.isAssignableFrom(type) : null) == true` in
  `IEnumerableConverterFactoryHelpers.java:219` — converter must lower `bool?` conditionals null-safely.
- Maven targets: `dotnet.text.json.Serialization.Tests.CollectionTests, DictionaryTests, DictionaryKeyPolicyTests,
  ArrayTests, StreamTests, NullTests, ObjectTests, CacheTests, JsonSerializerTests,
  dotnet.text.json.Tests.ImmutableCollectionsTests, CustomConverterTests`.

### Agent 5 — constructor/parameterized-constructor detection cluster
- Evidence: `UnsupportedOperationException: Deserialization of types without a parameterless constructor,
  a singular parameterized constructor, or a parameterized constructor annotated with 'JsonConstructorAttribute'
  is not supported. Type 'class ...ObjectTests$Indexer / ExtensionDataTests$ClassWithMultipleDictionaries /
  ReferenceHandlerTests$Employee / ExceptionTests$ClassWithUnicodePropertyName / NumberHandlingTests$ClassWith_LooseAttribute'`.
- Files: generated `JsonClassInfo.java`, `ObjectDefaultConverter.java`,
  `ObjectWithParameterizedConstructorConverter.java`, `JsonSerializerOptions.java`;
  converter attribute/metadata handling (`JsonConstructorAttribute`, `ConstructorInfo`, `Activator`).
- Maven targets: `ObjectTests, ExtensionDataTests, ReferenceHandlerTests, ExceptionTests, NumberHandlingTests,
  JsonSerializerTests, CacheTests, OptionsTests, CustomConverterTests`.

### Agent 6 — enum conversion cluster
- Evidence: `ClassCastException: class io.github.ningpp.compat.DayOfWeek cannot be cast to class java.lang.Integer`;
  `java.lang.Integer cannot be cast to class java.lang.Enum`;
  `NegativeEnum cannot be cast to Integer`; `SampleEnumByte` parameter conversion to long;
  `dotnet.text.json.Tests.EnumConverterTests`, `dotnet.text.json.Serialization.Tests.EnumTests/EnumConverterTests`.
- Files: `src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs`, `UnsignedTypeRewriter`,
  compat `EnumHelper.java`, generated enum `fromValue`/`_UNMAPPED` logic.
- Prior work: commits `42aa0766`, `ae75331e`; analysis entries Iteration 8/14/15.
- Maven targets: `dotnet.text.json.Tests.EnumConverterTests, dotnet.text.json.Serialization.Tests.EnumTests,
  dotnet.text.json.Serialization.Tests.EnumConverterTests, dotnet.text.json.Serialization.Tests.NumberHandlingTests`.

### Agent 7 — span/memory/stream/DateTime compat cluster
- Evidence: `ReadOnlySpan.get(int)` returns null Integer; `MemoryMarshal.getReference(ReadOnlySpan)` null;
  `ArgumentException: Stream is not writable`; `DateTimeParseException: Invalid DateTime format`
  (`1997-07-16T19:20+01:00`, `...Z`, 7 fractional digits); `IndexOutOfBoundsException: index=26, length=18/19`
  in CamelCaseTests/DateTimeConverterTests; `[Ljava.lang.Integer; cannot be cast to [I` in ReadValueTests.
- Files: compat `ReadOnlySpan.java`, `ReadOnlyMemory.java`, `MemoryMarshal.java`, `Stream` wrappers,
  `DateTime.java`/`DateTimeOffset.java`/`TimeSpan.java`, `MemoryExtensions.java`, `ArrayBufferWriter.java`;
  converter span/primitive-array mapping.
- Prior work: commits `4176d86f`, `60df97f9`, `ae75331e`, `22160dde`; analysis entries Iteration 12/13/16.
- Maven targets: `dotnet.text.json.Tests.DateTimeConverterTests, CamelCaseTests,
  dotnet.text.json.Serialization.Tests.StreamTests, SpanTests, ReadValueTests, PropertyNameTests,
  dotnet.text.json.Tests.DebuggerTests`.

### Agent 8 — InlineData/attribute constant-expression cluster
- Evidence: `Failed to convert String "int.MaxValue / 32 + 1" to type int` (BitStackTests);
  `"Threshold - 3"` (ValueTests); `"System.Byte.MaxValue + 4"` (JsonReaderStateAndOptionsTests);
  `"ClassWithExtensionPropertyNoGenericParameters.class"` (ExtensionDataTests/PropertyVisibilityTests);
  `SampleEnumByte`→long (Serialization.EnumTests).
- Root cause: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` falls back to `expr.ToString()`
  (~line 1075) for non-literal InlineData expressions; route such args to `BuildInlineDataDataMethod` (~line 1324).
- Prior work: commit `d463dabd`; analysis entry Iteration 9/18.
- Maven targets: `dotnet.text.json.Tests.BitStackTests, JsonReaderStateAndOptionsTests,
  dotnet.text.json.Serialization.Tests.ValueTests, ExtensionDataTests, PropertyVisibilityTests, EnumTests`.

## Coordination notes
- Spawn with `fork_turns=none` and a fully self-contained prompt.
- If an existing agent reports it already owns a pending cluster, reassign the duplicate.
- Do not spawn more than 3 children concurrently (4 slots incl. root).
