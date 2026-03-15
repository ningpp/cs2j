# EventFieldTransformer

**Source:** `src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs`

## 1. C# Language Feature Converted

`EventFieldTransformer` converts C# event field declarations to a Java listener pattern. A C# `event` is a multicast delegate that allows multiple subscribers.

The transformer generates:
1. A private `ArrayList<ListenerType>` backing field
2. An `addXxxListener(ListenerType l)` method
3. A `removeXxxListener(ListenerType l)` method
4. A `protected void fireXxx(...)` dispatcher method

```csharp
// C#
public event EventHandler<DataEventArgs> DataReceived;

// Java
private ArrayList<EventHandlerDataEventArgs> _dataReceivedListeners = new ArrayList<>();
public void addDataReceivedListener(EventHandlerDataEventArgs l) { _dataReceivedListeners.add(l); }
public void removeDataReceivedListener(EventHandlerDataEventArgs l) { _dataReceivedListeners.remove(l); }
protected void fireDataReceived(DataEventArgs args) {
    for (EventHandlerDataEventArgs l : _dataReceivedListeners) l.invoke(args);
}
```

## 2. Deep Analysis of Current Code Issues

### Issue 1 — Explicit `add`/`remove` accessor bodies are silently discarded

C# allows custom event accessor implementations:
```csharp
public event EventHandler Click {
    add    { lock(_lock) { _click += value; } }
    remove { lock(_lock) { _click -= value; } }
}
```

The transformer checks whether the event is an `EventFieldDeclarationSyntax` or `EventDeclarationSyntax`, but does not actually translate the custom accessor bodies. The body code is dropped and the standard listener boilerplate is emitted regardless, losing locking, validation, or weak-reference logic.

### Issue 2 — Backing `ArrayList` is not thread-safe

The generated add/remove/fire methods are not `synchronized`, yet concurrent invocations (a common use case for events) will produce `ConcurrentModificationException` when `fireXxx` iterates while another thread modifies the list. The correct collection is `java.util.concurrent.CopyOnWriteArrayList`, which allows safe iteration during mutation:
```java
private CopyOnWriteArrayList<ListenerType> _listeners = new CopyOnWriteArrayList<>();
```

### Issue 3 — `static` events are not handled

```csharp
public static event EventHandler ApplicationStarted;
```

The transformer generates instance members (non-static backing field, non-static add/remove/fire methods) regardless of the `static` modifier on the event. The generated Java code will not compile if the event is used from a static context.

### Issue 4 — `fireXxx` visibility is always `protected`

```csharp
protected void fireDataReceived(...) { ... }
```

The visibility of `fireXxx` is hardcoded to `protected`. If the event itself is `private` (raised only by the declaring class), `protected` is too permissive. If the event is `internal`, the fire method should be package-private. The visibility should match or be more restrictive than the event's declared visibility.

### Issue 5 — Event type name mangling loses generic parameters

For `event EventHandler<DataEventArgs>`, the transformer creates a listener interface name by replacing `<` and `>` with empty strings or concatenating, producing `EventHandlerDataEventArgs`. This is not a valid Java class name and will never resolve in the generated `import` list. The correct approach is to map `EventHandler<T>` to a functional interface like `Consumer<T>` or generate a typed listener interface.

### Issue 6 — No `import` statement added for `ArrayList`

The transformer adds a backing field of type `ArrayList` without ensuring `java.util.ArrayList` is in the imports. Depending on whether `ArrayList` is already imported elsewhere in the file, this may produce compilation errors.

## 3. Proposed Fixes

### Fix 1 — Translate explicit accessor bodies

When `EventDeclarationSyntax` has an `AddAccessorDeclaration` or `RemoveAccessorDeclaration` with a body, translate those bodies using the statement transformer:
```csharp
if (eventDecl.AccessorList != null)
{
    var addBody = eventDecl.AccessorList.Accessors
        .FirstOrDefault(a => a.IsKind(SyntaxKind.AddAccessorDeclaration))?.Body;
    if (addBody != null)
        addMethod.Body = TransformStatements(addBody, context);
}
```

### Fix 2 — Switch backing list to `CopyOnWriteArrayList`

```java
private CopyOnWriteArrayList<ListenerType> _listeners = new CopyOnWriteArrayList<>();
```

Add `import java.util.concurrent.CopyOnWriteArrayList;`.

### Fix 3 — Apply `static` modifier when the event is static

```csharp
if (HasStaticModifier(eventDecl))
{
    backingField.Modifiers.Add("static");
    addMethod.Modifiers.Add("static");
    removeMethod.Modifiers.Add("static");
    fireMethod.Modifiers.Add("static");
}
```

### Fix 4 — Match `fireXxx` visibility to event visibility

```csharp
string eventVisibility = GetEventVisibility(eventDecl);
string fireVisibility  = eventVisibility == "public" ? "protected" : eventVisibility;
fireMethod.Modifiers.Add(fireVisibility);
```

### Fix 5 — Use functional interface for generic event handler types

Map `EventHandler<T>` to `Consumer<T>` (or a generated `XxxListener` interface) rather than concatenating the generic argument into the class name.

### Fix 6 — Always add the `ArrayList`/`CopyOnWriteArrayList` import

```csharp
context.AddImport("java.util.concurrent.CopyOnWriteArrayList");
```

## 4. Commit Changes

```
fix(event): translate explicit accessor bodies, use CopyOnWriteArrayList for thread safety, propagate static modifier, fix fireXxx visibility, and map generic EventHandler to Consumer<T>
```
