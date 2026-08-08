package io.github.ningpp.compat;

public final class RuntimeInformation {
    private RuntimeInformation() {
    }

    public static String getFrameworkDescription() {
        return "Java " + System.getProperty("java.version", "");
    }

    public static boolean isOSPlatform(OSPlatform osPlatform) {
        if (osPlatform == null) {
            return false;
        }
        String osName = System.getProperty("os.name", "").toLowerCase();
        switch (osPlatform.toString()) {
            case "Windows":
                return osName.contains("win");
            case "OSX":
                return osName.contains("mac");
            case "Linux":
                return osName.contains("linux");
            case "FreeBSD":
                return osName.contains("freebsd");
            case "Android":
                return osName.contains("android");
            case "iOS":
                return osName.contains("ios");
            default:
                // e.g. BROWSER, Wasm, Fuchsia — never true on the JVM
                return false;
        }
    }

    public static Architecture getProcessArchitecture() {
        String arch = System.getProperty("os.arch", "").toLowerCase();
        switch (arch) {
            case "amd64":
            case "x86_64":
                return Architecture.X64;
            case "aarch64":
            case "arm64":
                return Architecture.Arm64;
            case "x86":
            case "i386":
            case "i486":
            case "i586":
            case "i686":
                return Architecture.X86;
            case "arm":
            case "armv7":
                return Architecture.Arm;
            case "riscv64":
                return Architecture.RiscV64;
            case "s390x":
                return Architecture.S390x;
            case "ppc64le":
                return Architecture.Ppc64le;
            case "loongarch64":
                return Architecture.LoongArch64;
            default:
                return Architecture.X64;
        }
    }
}
