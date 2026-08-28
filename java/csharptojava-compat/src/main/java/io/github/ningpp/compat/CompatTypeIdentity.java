package io.github.ningpp.compat;

import java.util.HashMap;
import java.util.Map;

/**
 * Type-identity registry for compat classes that stand in for BCL types living in
 * a specific .NET assembly.
 *
 * The converted code keeps comparing types by their C# identity — e.g.
 * {@code IEnumerableConverterFactoryHelpers.IsImmutableEnumerableType} switches on
 * {@code Type.GetGenericTypeDefinition().FullName} ("System.Collections.Immutable.ImmutableHashSet`1")
 * and gates on {@code Type.Assembly.FullName} starting with "System.Collections.Immutable,".
 * Java erasure makes a compat class literal (ImmutableHashSet.class) carry none of that,
 * so this registry supplies the mapping in both directions:
 *
 * <ul>
 *   <li>{@code getFullName} — compat class → C# arity-suffixed full name</li>
 *   <li>{@code getAssemblyFullName} — compat class → owning assembly's full name</li>
 *   <li>{@code resolve} — C# namespace-qualified name (no arity) → compat class</li>
 * </ul>
 */
public final class CompatTypeIdentity {
    /** C# FullName of System.Collections.Immutable, matching the "name," prefix check. */
    public static final String IMMUTABLE_ASSEMBLY_FULL_NAME =
        "System.Collections.Immutable, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
    /** C# namespace of the registered immutable collection types. */
    public static final String IMMUTABLE_NAMESPACE = "System.Collections.Immutable";

    private static final Map<Class<?>, String> FULL_NAMES = new HashMap<>();
    private static final Map<String, Class<?>> BY_SIMPLE_NAME = new HashMap<>();

    static {
        register("IImmutableList", IImmutableList.class);
        register("IImmutableSet", IImmutableSet.class);
        register("IImmutableStack", IImmutableStack.class);
        register("IImmutableQueue", IImmutableQueue.class);
        register("IImmutableDictionary", IImmutableDictionary.class);
        register("ImmutableList", ImmutableList.class);
        register("ImmutableArray", ImmutableArray.class);
        register("ImmutableHashSet", ImmutableHashSet.class);
        register("ImmutableSortedSet", ImmutableSortedSet.class);
        register("ImmutableStack", ImmutableStack.class);
        register("ImmutableQueue", ImmutableQueue.class);
        register("ImmutableDictionary", ImmutableDictionary.class);
        register("ImmutableSortedDictionary", ImmutableSortedDictionary.class);
    }

    private CompatTypeIdentity() {
    }

    private static void register(String simpleName, Class<?> clazz) {
        int arity = clazz.getTypeParameters().length;
        String arityMarker = arity > 0 ? "`" + arity : "";
        FULL_NAMES.put(clazz, IMMUTABLE_NAMESPACE + "." + simpleName + arityMarker);
        BY_SIMPLE_NAME.put(simpleName, clazz);
    }

    /**
     * Returns the C#-style full name for a registered compat class (e.g.
     * "System.Collections.Immutable.ImmutableHashSet`1"), or null when the class
     * has no registered identity.
     */
    public static String getFullName(Class<?> clazz) {
        return clazz == null ? null : FULL_NAMES.get(clazz);
    }

    /** True when the class is a registered System.Collections.Immutable compat type. */
    public static boolean isImmutableCollection(Class<?> clazz) {
        return clazz != null && FULL_NAMES.containsKey(clazz);
    }

    /** True when the class is a registered immutable type implementing IEnumerable (all but none — the dictionary interfaces also enumerate KVPairs). */
    public static boolean isImmutableEnumerable(Class<?> clazz) {
        return isImmutableCollection(clazz);
    }

    /**
     * True when the class itself, or any supertype it inherits/implements, is a
     * registered immutable collection interface or class. Converted user classes
     * implement the compat immutable interfaces directly (e.g. a test wrapper
     * implementing IImmutableSet&lt;String&gt;), and in C# such classes implement
     * IEnumerable&lt;T&gt; transitively, so the JSON serializer's
     * IEnumerable-assignability check must treat them the same way.
     */
    public static boolean implementsImmutableInterface(Class<?> clazz) {
        if (clazz == null) {
            return false;
        }
        if (FULL_NAMES.containsKey(clazz)) {
            return true;
        }
        for (Class<?> iface : clazz.getInterfaces()) {
            if (implementsImmutableInterface(iface)) {
                return true;
            }
        }
        Class<?> superclass = clazz.getSuperclass();
        return superclass != null && implementsImmutableInterface(superclass);
    }

    /** Returns the full name of the assembly owning a registered compat class, or null. */
    public static String getAssemblyFullName(Class<?> clazz) {
        return isImmutableCollection(clazz) ? IMMUTABLE_ASSEMBLY_FULL_NAME : null;
    }

    /**
     * Resolves a namespace-qualified C# name ("System.Collections.Immutable.ImmutableHashSet",
     * no arity marker — matching how the constructing-type lookup names builder types)
     * back to the compat class.
     */
    public static Class<?> resolve(String csharpName) {
        if (csharpName == null) {
            return null;
        }
        if (!csharpName.startsWith(IMMUTABLE_NAMESPACE + ".")) {
            return null;
        }
        return BY_SIMPLE_NAME.get(csharpName.substring(IMMUTABLE_NAMESPACE.length() + 1));
    }
}
