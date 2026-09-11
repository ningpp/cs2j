import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.stream.Stream;

/**
 * Finds converter emission defects of the MemberAccessor kind: a subclass "override" whose
 * signature differs from the supertype method by an injected {@code Class<?>} type-parameter
 * token, so it does not actually override anything and the call dispatches to the base body
 * (which throws UnsupportedOperationException).
 *
 * <p>Usage: {@code java OverrideScan <classes-dir> [<classes-dir> ...]}</p>
 */
public final class OverrideScan {

    public static void main(String[] args) throws Exception {
        List<Path> roots = new ArrayList<>();
        for (String arg : args) {
            roots.add(Path.of(arg));
        }
        ClassLoader loader = OverrideScan.class.getClassLoader();
        List<String> classNames = new ArrayList<>();
        for (Path root : roots) {
            if (!Files.isDirectory(root)) {
                continue;
            }
            try (Stream<Path> stream = Files.walk(root)) {
                stream.filter(p -> p.toString().endsWith(".class"))
                    .forEach(p -> classNames.add(toClassName(root, p)));
            }
        }
        System.out.println("scanning " + classNames.size() + " classes from " + roots);

        Map<String, Set<String>> findings = new LinkedHashMap<>();
        int loaded = 0;
        for (String name : classNames) {
            Class<?> type;
            try {
                type = Class.forName(name, false, loader);
                loaded++;
            } catch (Throwable t) {
                continue; // unresolvable (missing optional deps, preview classes)
            }
            if (type.isInterface() || type.isEnum() || type.isAnnotation() || type.isAnonymousClass()) {
                continue;
            }
            for (Class<?> supertype : supertypes(type)) {
                if (supertype == Object.class || supertype.isInterface() && !type.getName().startsWith(supertype.getName().split("\\.")[0])) {
                    // still interesting: interfaces from the same generated tree are checked below
                    if (supertype == Object.class) {
                        continue;
                    }
                }
                for (java.lang.reflect.Method base : supertype.getDeclaredMethods()) {
                    if (base.isSynthetic() || java.lang.reflect.Modifier.isStatic(base.getModifiers())) {
                        continue;
                    }
                    if (declaresOverride(type, base)) {
                        continue;
                    }
                    java.lang.reflect.Method tokenVariant = findTokenVariant(type, base);
                    if (tokenVariant != null) {
                        findings.computeIfAbsent(type.getName(), k -> new TreeSet<>())
                            .add(base.getName() + "  base(" + describe(base.getParameterTypes()) + ")"
                                + "  vs  " + type.getSimpleName() + "(" + describe(tokenVariant.getParameterTypes()) + ")"
                                + "  [" + supertype.getSimpleName() + "]");
                    }
                }
            }
        }
        System.out.println("loaded " + loaded + " classes, " + findings.size() + " classes with token-mismatched overrides");
        findings.entrySet().stream()
            .sorted(Comparator.comparing(e -> e.getKey()))
            .forEach(e -> {
                System.out.println("* " + e.getKey());
                e.getValue().forEach(v -> System.out.println("    " + v));
            });
    }

    static String toClassName(Path root, Path classFile) {
        String relative = root.relativize(classFile).toString();
        return relative.substring(0, relative.length() - ".class".length())
            .replace(java.io.File.separatorChar, '.')
            .replace('/', '.');
    }

    static boolean declaresOverride(Class<?> type, java.lang.reflect.Method base) {
        for (java.lang.reflect.Method candidate : type.getDeclaredMethods()) {
            if (candidate.getName().equals(base.getName())
                && java.util.Arrays.equals(candidate.getParameterTypes(), base.getParameterTypes())) {
                return true;
            }
        }
        return false;
    }

    /**
     * A method with the same name whose parameters are the base's with one extra leading Class
     * token, or the base's minus that token (the MemberAccessor case: the base declares the
     * token, the intended override does not).
     */
    static java.lang.reflect.Method findTokenVariant(Class<?> type, java.lang.reflect.Method base) {
        Class<?>[] baseParams = base.getParameterTypes();
        boolean baseHasToken = baseParams.length > 0 && baseParams[0] == Class.class;
        for (java.lang.reflect.Method candidate : type.getDeclaredMethods()) {
            if (!candidate.getName().equals(base.getName())) {
                continue;
            }
            Class<?>[] params = candidate.getParameterTypes();
            if (params.length == baseParams.length + 1 && params[0] == Class.class
                && java.util.Arrays.equals(
                    java.util.Arrays.copyOfRange(params, 1, params.length), baseParams)) {
                return candidate;
            }
            if (baseHasToken && params.length == baseParams.length - 1
                && java.util.Arrays.equals(
                    java.util.Arrays.copyOfRange(baseParams, 1, baseParams.length), params)) {
                return candidate;
            }
        }
        return null;
    }

    static Set<Class<?>> supertypes(Class<?> type) {
        Set<Class<?>> result = new LinkedHashSet<>();
        for (Class<?> c = type.getSuperclass(); c != null && c != Object.class; c = c.getSuperclass()) {
            result.add(c);
        }
        collectInterfaces(type, result);
        return result;
    }

    static void collectInterfaces(Class<?> type, Set<Class<?>> result) {
        for (Class<?> i : type.getInterfaces()) {
            result.add(i);
            collectInterfaces(i, result);
        }
        if (type.getSuperclass() != null) {
            collectInterfaces(type.getSuperclass(), result);
        }
    }

    static String describe(Class<?>[] params) {
        StringBuilder sb = new StringBuilder();
        for (Class<?> p : params) {
            if (sb.length() > 0) {
                sb.append(", ");
            }
            sb.append(p.getSimpleName());
        }
        return sb.toString();
    }
}
