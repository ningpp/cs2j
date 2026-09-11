package io.github.ningpp.compat;

import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Declaration order of a type's members, recovered from its class file.
 *
 * <p>C# reflection ({@code Type.GetProperties()}, {@code Type.GetFields()},
 * {@code Type.GetMethods()}, {@code Type.GetConstructors()}) returns members in metadata
 * declaration order, and the JSON serializer's property cache is insertion-ordered, so JSON
 * member order — and any "first member that matches wins" decision — depends on it. On the
 * Java side neither {@code Class#getMethods()} nor {@code Class#getDeclaredMethods()} /
 * {@code getDeclaredConstructors()} guarantees an order and HotSpot's order actually changes
 * between runs of the same JVM (observed on {@code dotnet.text.json.Tests.NegativeEnumClass}:
 * {@code [getValue1, getValue2]} in one run, {@code [getValue2, getValue1]} in the next),
 * which made order-sensitive tests flaky. The class file's member table is written by javac
 * in source declaration order — the same order the converter emitted the members in — so it
 * is the faithful, stable key.</p>
 *
 * <p>Classes whose bytes cannot be read (JDK-internal types, dynamically generated classes)
 * yield empty tables; callers then order by their remaining tie-breakers, which keeps
 * results deterministic.</p>
 */
public final class DeclarationOrder {

    private DeclarationOrder() { }

    private static final class MemberTables {
        /** method name → index of its first class-file entry. */
        final Map<String, Integer> methodsByName;
        /** method name + descriptor → class-file index (covers {@code <init>}). */
        final Map<String, Integer> methodsByKey;
        /** field name + descriptor → class-file index. */
        final Map<String, Integer> fieldsByKey;
        /** field name → index of its first class-file entry. */
        final Map<String, Integer> fieldsByName;

        MemberTables(Map<String, Integer> methodsByName, Map<String, Integer> methodsByKey,
                     Map<String, Integer> fieldsByKey, Map<String, Integer> fieldsByName) {
            this.methodsByName = methodsByName;
            this.methodsByKey = methodsByKey;
            this.fieldsByKey = fieldsByKey;
            this.fieldsByName = fieldsByName;
        }

        static MemberTables empty() {
            return new MemberTables(Map.of(), Map.of(), Map.of(), Map.of());
        }
    }

    private static final ClassValue<MemberTables> TABLES = new ClassValue<>() {
        @Override
        protected MemberTables computeValue(Class<?> type) {
            return readTables(type);
        }
    };

    /** Index of a method's first entry of that name in its class-file method table; -1 if unknown. */
    public static int methodIndex(Class<?> declaringClass, String methodName) {
        if (declaringClass == null || methodName == null) {
            return -1;
        }
        Integer index = TABLES.get(declaringClass).methodsByName.get(methodName);
        return index == null ? -1 : index;
    }

    /**
     * Index of an exact method (overload-aware: matched by name and descriptor) within its
     * declaring class's class-file method table; falls back to the first method of that name.
     */
    public static int methodIndex(Class<?> declaringClass, java.lang.reflect.Method method) {
        if (declaringClass == null || method == null) {
            return -1;
        }
        Integer exact = TABLES.get(declaringClass).methodsByKey.get(method.getName()
            + descriptor(method.getParameterTypes(), method.getReturnType()));
        if (exact != null) {
            return exact;
        }
        return methodIndex(declaringClass, method.getName());
    }

    /** Index of a constructor (matched by name and descriptor) within its class-file table. */
    public static int constructorIndex(Class<?> declaringClass, Class<?>[] parameterTypes) {
        if (declaringClass == null) {
            return -1;
        }
        Integer index = TABLES.get(declaringClass).methodsByKey
            .get("<init>" + descriptor(parameterTypes, void.class));
        return index == null ? -1 : index;
    }

    /** Index of a field's first entry of that name in its class-file field table; -1 if unknown. */
    public static int fieldIndex(Class<?> declaringClass, String fieldName) {
        if (declaringClass == null || fieldName == null) {
            return -1;
        }
        Integer index = TABLES.get(declaringClass).fieldsByName.get(fieldName);
        return index == null ? -1 : index;
    }

    /**
     * Distance between {@code root} and {@code declaring} along the superclass chain
     * (0 = declared by {@code root} itself), so more derived members sort first — C#
     * returns a type's own members before the inherited ones. Members declared by an
     * interface sort after all class-declared members.
     */
    public static int hierarchyDepth(Class<?> root, Class<?> declaring) {
        if (root == null || declaring == null) {
            return Integer.MAX_VALUE;
        }
        int depth = 0;
        for (Class<?> current = root; current != null; current = current.getSuperclass()) {
            if (current == declaring) {
                return depth;
            }
            depth++;
        }
        return 10_000;
    }

    private static MemberTables readTables(Class<?> type) {
        byte[] bytes = readClassBytes(type);
        if (bytes == null) {
            return MemberTables.empty();
        }
        try {
            return parse(bytes);
        } catch (RuntimeException e) {
            return MemberTables.empty();
        }
    }

    private static byte[] readClassBytes(Class<?> type) {
        String resource = "/" + type.getName().replace('.', '/') + ".class";
        try (InputStream in = type.getResourceAsStream(resource)) {
            return in == null ? null : in.readAllBytes();
        } catch (IOException | RuntimeException e) {
            return null;
        }
    }

    private static MemberTables parse(byte[] data) {
        Cursor cursor = new Cursor(data);
        if (cursor.u4() != 0xCAFEBABE) {
            throw new IllegalStateException("Not a class file");
        }
        cursor.u2(); // minor version
        cursor.u2(); // major version
        String[] utf8 = readConstantPool(cursor);
        cursor.u2(); // access_flags
        cursor.u2(); // this_class
        cursor.u2(); // super_class
        cursor.skip(cursor.u2() * 2); // interfaces
        List<MemberKey> fieldKeys = readMemberKeys(cursor, utf8);
        List<MemberKey> methodKeys = readMemberKeys(cursor, utf8);
        return new MemberTables(
            firstIndexOf(methodKeys, true),
            indexOf(methodKeys),
            indexOf(fieldKeys),
            firstIndexOf(fieldKeys, false));
    }

    /** A member's declared name plus its JVM name+descriptor key, in class-file order. */
    private static final class MemberKey {
        final String name;
        final String key;

        MemberKey(String name, String key) {
            this.name = name;
            this.key = key;
        }
    }

    /** name + descriptor for each member, in class-file (declaration) order. */
    private static List<MemberKey> readMemberKeys(Cursor cursor, String[] utf8) {
        int count = cursor.u2();
        List<MemberKey> keys = new ArrayList<>(count);
        for (int i = 0; i < count; i++) {
            cursor.u2(); // access_flags
            String name = utf8[cursor.u2()];
            String descriptor = utf8[cursor.u2()];
            int attributes = cursor.u2();
            for (int a = 0; a < attributes; a++) {
                cursor.u2(); // attribute_name_index
                cursor.skip(cursor.u4());
            }
            keys.add(new MemberKey(name, name + descriptor));
        }
        return keys;
    }

    private static Map<String, Integer> indexOf(List<MemberKey> members) {
        Map<String, Integer> byKey = new HashMap<>();
        for (int i = 0; i < members.size(); i++) {
            byKey.putIfAbsent(members.get(i).key, i);
        }
        return byKey;
    }

    private static Map<String, Integer> firstIndexOf(List<MemberKey> members, boolean methods) {
        Map<String, Integer> byName = new HashMap<>();
        for (int i = 0; i < members.size(); i++) {
            String name = members.get(i).name;
            if (methods && name.startsWith("<")) {
                continue; // constructors and static initializers are not methods
            }
            byName.putIfAbsent(name, i);
        }
        return byName;
    }

    private static String[] readConstantPool(Cursor cursor) {
        int count = cursor.u2();
        String[] utf8 = new String[count];
        for (int i = 1; i < count; i++) {
            int tag = cursor.u1();
            switch (tag) {
                case 1 -> utf8[i] = cursor.utf8();
                case 7, 8, 16, 19, 20 -> cursor.skip(2);
                case 15 -> cursor.skip(3);
                case 3, 4, 9, 10, 11, 12, 17, 18 -> cursor.skip(4);
                case 5, 6 -> {
                    cursor.skip(8);
                    i++; // long/double occupy two constant-pool slots
                }
                default -> throw new IllegalStateException("Unknown constant pool tag " + tag);
            }
        }
        return utf8;
    }

    private static String descriptor(Class<?>[] parameterTypes, Class<?> returnType) {
        StringBuilder builder = new StringBuilder("(");
        if (parameterTypes != null) {
            for (Class<?> parameterType : parameterTypes) {
                builder.append(typeDescriptor(parameterType));
            }
        }
        return builder.append(')').append(typeDescriptor(returnType)).toString();
    }

    private static String typeDescriptor(Class<?> type) {
        if (type == null) return "Ljava/lang/Object;";
        if (type == void.class) return "V";
        if (type == boolean.class) return "Z";
        if (type == byte.class) return "B";
        if (type == char.class) return "C";
        if (type == short.class) return "S";
        if (type == int.class) return "I";
        if (type == long.class) return "J";
        if (type == float.class) return "F";
        if (type == double.class) return "D";
        if (type.isArray()) return "[" + typeDescriptor(type.getComponentType());
        return "L" + type.getName().replace('.', '/') + ";";
    }

    private static final class Cursor {
        private final byte[] data;
        private int position;

        Cursor(byte[] data) {
            this.data = data;
        }

        int u1() {
            return data[position++] & 0xFF;
        }

        int u2() {
            int value = ((data[position] & 0xFF) << 8) | (data[position + 1] & 0xFF);
            position += 2;
            return value;
        }

        int u4() {
            int value = ((data[position] & 0xFF) << 24)
                | ((data[position + 1] & 0xFF) << 16)
                | ((data[position + 2] & 0xFF) << 8)
                | (data[position + 3] & 0xFF);
            position += 4;
            return value;
        }

        String utf8() {
            int length = u2();
            String value = new String(data, position, length, java.nio.charset.StandardCharsets.UTF_8);
            position += length;
            return value;
        }

        void skip(int bytes) {
            position += bytes;
        }
    }
}
