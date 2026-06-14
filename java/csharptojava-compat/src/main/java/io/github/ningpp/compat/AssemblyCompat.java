package io.github.ningpp.compat;

import java.net.URL;
import java.net.URLClassLoader;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;

/**
 * Minimal compat stub for System.Reflection.Assembly.
 * Supports the API surface used by CorrectnessTest.
 */
public class AssemblyCompat {
    private final ClassLoader classLoader;
    private final String location;

    private AssemblyCompat(ClassLoader classLoader, String location) {
        this.classLoader = classLoader;
        this.location = location;
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

    /** Mirrors C# Assembly.GetManifestResourceStream(name) */
    public java.io.InputStream getManifestResourceStream(String name) {
        java.io.InputStream stream = classLoader.getResourceAsStream(name);
        if (stream == null) {
            stream = AssemblyCompat.class.getClassLoader().getResourceAsStream(name);
        }
        return stream;
    }

    /** Nested type for AssemblyName compat */
    public static class AssemblyNameCompat {
        private final String name;

        AssemblyNameCompat(String name) {
            this.name = name;
        }

        @Override
        public String toString() {
            return name;
        }
    }
}
