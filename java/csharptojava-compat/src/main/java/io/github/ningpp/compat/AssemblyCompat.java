package io.github.ningpp.compat;

import java.net.URL;
import java.net.URLClassLoader;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Minimal compat stub for System.Reflection.Assembly.
 * Supports the API surface used by CorrectnessTest.
 */
public class AssemblyCompat {
    private static final Map<String, AssemblyCompat> s_cache = new ConcurrentHashMap<>();

    private final ClassLoader classLoader;
    private final String location;

    private AssemblyCompat(ClassLoader classLoader, String location) {
        this.classLoader = classLoader;
        this.location = location;
    }

    /** Mirrors C# Assembly.Load(AssemblyName) */
    public static AssemblyCompat load(AssemblyNameCompat name) {
        return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), name.getName());
    }

    /** Mirrors C# Assembly.LoadFrom(path) */
    public static AssemblyCompat loadFrom(String path) {
        try {
            File file = new File(path);
            URL[] urls = { file.toURI().toURL() };
            URLClassLoader loader = new URLClassLoader(urls, AssemblyCompat.class.getClassLoader());
            return new AssemblyCompat(loader, path);
        } catch (Exception e) {
            throw new RuntimeException("Failed to load assembly from: " + path, e);
        }
    }

    /** Mirrors typeof(T).Assembly.GetManifestResourceStream(name) for classpath resources. */
    public static UnmanagedMemoryStream getManifestResourceStream(Class<?> anchorType, String name) {
        String packagePrefix = anchorType.getPackageName().replace('.', '/');
        String packageResource = packagePrefix.isEmpty() ? name : packagePrefix + "/" + name;
        InputStream stream = anchorType.getClassLoader().getResourceAsStream(packageResource);
        if (stream == null)
            stream = anchorType.getClassLoader().getResourceAsStream(name);
        if (stream == null)
            throw new IllegalArgumentException("Manifest resource not found: " + name);

        try (InputStream input = stream) {
            return new UnmanagedMemoryStream(input.readAllBytes());
        } catch (IOException e) {
            throw new java.io.UncheckedIOException(e);
        }
    }

    /** Mirrors C# Assembly.GetType(name) */
    public Class<?> getClass(String name) {
        // Registered compat identities (System.Collections.Immutable et al.) are
        // addressed by their C# namespace-qualified names in generated code, e.g.
        // the immutable-collection factories ask this assembly for
        // "System.Collections.Immutable.ImmutableHashSet".
        Class<?> byIdentity = CompatTypeIdentity.resolve(name);
        if (byIdentity != null) {
            return byIdentity;
        }
        try {
            return classLoader.loadClass(name);
        } catch (ClassNotFoundException e) {
            return null;
        }
    }

    /** Mirrors C# Assembly.GetTypes() — returns all types from the assembly */
    public Class<?>[] getTypes() {
        // Simplified: can't enumerate all types from a URLClassLoader easily
        return new Class<?>[0];
    }

    /** Mirrors C# Assembly.GetName() */
    public AssemblyNameCompat getPackage() {
        return new AssemblyNameCompat(location);
    }

    /** Mirrors C# Assembly.FullName */
    public String getFullName() {
        return location;
    }

    /** Mirrors C# Assembly.Location */
    public String getLocation() {
        return location;
    }

    /** Mirrors C# Assembly.GetExecutingAssembly() */
    public static AssemblyCompat getExecutingAssembly() {
        return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), "executing");
    }

    /** Mirrors C# Assembly.GetEntryAssembly() */
    public static AssemblyCompat getEntryAssembly() {
        return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), "entry");
    }

    /** Mirrors C# Assembly.LoadFile(path) */
    public static AssemblyCompat loadFile(String path) {
        return loadFrom(path);
    }

    /** Mirrors C# Assembly.LoadWithPartialName(name) */
    public static AssemblyCompat loadWithPartialName(String name) {
        return new AssemblyCompat(AssemblyCompat.class.getClassLoader(), name);
    }

    /** Mirrors C# Assembly.IsDynamic */
    public boolean getIsDynamic() {
        return false;
    }

    /** Creates an AssemblyCompat representing the assembly that declares the given class.
     *  Mirrors C# {@code typeof(T).Assembly} / {@code GetType().Assembly}. */
    public static AssemblyCompat fromClass(Class<?> clazz) {
        // Compat stand-ins for BCL types report the assembly of the type they model,
        // so generated checks like Assembly.FullName.StartsWith("System.Collections.Immutable,")
        // behave as they would on the original C# types.
        String identityAssembly = CompatTypeIdentity.getAssemblyFullName(clazz);
        if (identityAssembly != null) {
            return s_cache.computeIfAbsent(identityAssembly, k -> new AssemblyCompat(clazz.getClassLoader(), k));
        }
        java.security.ProtectionDomain domain = clazz.getProtectionDomain();
        java.net.URL codeSource = domain == null ? null : domain.getCodeSource() == null ? null : domain.getCodeSource().getLocation();
        String key = codeSource != null ? codeSource.toString() : String.valueOf(clazz.getClassLoader());
        return s_cache.computeIfAbsent(key, k -> new AssemblyCompat(clazz.getClassLoader(), k));
    }

    /** Creates an AssemblyCompat from a class loader and location/name. */
    public static AssemblyCompat fromClassLoader(ClassLoader classLoader, String location) {
        return new AssemblyCompat(classLoader, location);
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof AssemblyCompat other)) return false;
        return Objects.equals(classLoader, other.classLoader)
            && Objects.equals(location, other.location);
    }

    @Override
    public int hashCode() {
        return Objects.hash(classLoader, location);
    }

    /** Mirrors C# Assembly.GetManifestResourceStream(name) — returns StreamWrapper
     *  so the result is directly assignable to the Java mapping of System.IO.Stream. */
        /** Mirrors C# Assembly.GetManifestResourceNames(). */
    public String[] getManifestResourceNames() {
        return new String[0];
    }

    /** Mirrors C# Assembly.GetManifestResourceStream(name) — returns StreamWrapper
     *  so the result is directly assignable to the Java mapping of System.IO.Stream. */
    public StreamWrapper getManifestResourceStream(String name) {
        java.io.InputStream stream = classLoader.getResourceAsStream(name);
        if (stream == null) {
            stream = AssemblyCompat.class.getClassLoader().getResourceAsStream(name);
        }
        return StreamWrapper.of(stream);
    }

    /** Nested type for AssemblyName compat */
    public static class AssemblyNameCompat {
        private String name;
        private String codeBase;
        private CultureInfo cultureInfo;

        public AssemblyNameCompat(String name) {
            this.name = name;
        }

        public String getName() {
            return name;
        }

        public void setName(String name) {
            this.name = name;
        }

        public String getCodeBase() {
            return codeBase;
        }

        public void setCodeBase(String codeBase) {
            this.codeBase = codeBase;
        }

        public CultureInfo getCultureInfo() {
            return cultureInfo;
        }

        public void setCultureInfo(CultureInfo cultureInfo) {
            this.cultureInfo = cultureInfo;
        }

        /** Mirrors C# AssemblyName.GetPublicKeyToken() */
        public byte[] getPublicKeyToken() {
            return null;
        }

        @Override
        public String toString() {
            return name;
        }
    }
}
