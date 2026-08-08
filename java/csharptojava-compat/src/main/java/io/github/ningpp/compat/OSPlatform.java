package io.github.ningpp.compat;

/**
 * Compat for System.Runtime.InteropServices.OSPlatform.
 * Identifies an operating system platform, mirroring the .NET API surface.
 */
public final class OSPlatform {
    private final String name;

    private OSPlatform(String name) {
        this.name = name;
    }

    public static final OSPlatform Windows = new OSPlatform("Windows");
    public static final OSPlatform Linux = new OSPlatform("Linux");
    public static final OSPlatform OSX = new OSPlatform("OSX");
    public static final OSPlatform FreeBSD = new OSPlatform("FreeBSD");
    public static final OSPlatform Browser = new OSPlatform("BROWSER");
    public static final OSPlatform Android = new OSPlatform("Android");
    public static final OSPlatform iOS = new OSPlatform("iOS");
    public static final OSPlatform tvOS = new OSPlatform("tvOS");
    public static final OSPlatform watchOS = new OSPlatform("watchOS");
    public static final OSPlatform Fuchsia = new OSPlatform("Fuchsia");
    public static final OSPlatform Wasm = new OSPlatform("Wasm");
    public static final OSPlatform VisionOS = new OSPlatform("VisionOS");
    public static final OSPlatform LinuxBionic = new OSPlatform("LinuxBionic");

    public static OSPlatform create(String osPlatform) {
        if (osPlatform == null) throw new NullPointerException("osPlatform");
        for (OSPlatform known : new OSPlatform[] {
            Windows, Linux, OSX, FreeBSD, Browser, Android, iOS, tvOS, watchOS, Fuchsia, Wasm, VisionOS, LinuxBionic
        }) {
            if (known.name.equals(osPlatform)) {
                return known;
            }
        }
        return new OSPlatform(osPlatform);
    }

    public String getName() {
        return name;
    }

    @Override
    public String toString() {
        return name;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof OSPlatform)) return false;
        return name.equals(((OSPlatform) obj).name);
    }

    @Override
    public int hashCode() {
        return name.hashCode();
    }
}
