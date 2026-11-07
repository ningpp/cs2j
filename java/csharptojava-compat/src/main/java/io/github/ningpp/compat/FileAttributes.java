package io.github.ningpp.compat;

/**
 * Compat for System.IO.FileAttributes (flags) — emitted with the converter's
 * value-enum shape because the C# enum has explicit, non-ordinal values.
 */
public final class FileAttributes {
    private final String __cs2jName;
    private final int __cs2jOrdinal;
    private final int __cs2jValue;

    public static final FileAttributes None = new FileAttributes("None", 0, 0);
    public static final FileAttributes ReadOnly = new FileAttributes("ReadOnly", 1, 1);
    public static final FileAttributes Hidden = new FileAttributes("Hidden", 2, 2);
    public static final FileAttributes System = new FileAttributes("System", 3, 4);
    public static final FileAttributes Directory = new FileAttributes("Directory", 4, 16);
    public static final FileAttributes Archive = new FileAttributes("Archive", 5, 32);
    public static final FileAttributes Device = new FileAttributes("Device", 6, 64);
    public static final FileAttributes Normal = new FileAttributes("Normal", 7, 128);
    public static final FileAttributes Temporary = new FileAttributes("Temporary", 8, 256);
    public static final FileAttributes SparseFile = new FileAttributes("SparseFile", 9, 512);
    public static final FileAttributes ReparsePoint = new FileAttributes("ReparsePoint", 10, 1024);
    public static final FileAttributes Compressed = new FileAttributes("Compressed", 11, 2048);
    public static final FileAttributes Offline = new FileAttributes("Offline", 12, 4096);
    public static final FileAttributes NotContentIndexed = new FileAttributes("NotContentIndexed", 13, 8192);
    public static final FileAttributes Encrypted = new FileAttributes("Encrypted", 14, 16384);
    public static final FileAttributes IntegrityStream = new FileAttributes("IntegrityStream", 15, 32768);
    public static final FileAttributes NoScrubData = new FileAttributes("NoScrubData", 16, 131072);

    private static final FileAttributes[] __cs2jValues = new FileAttributes[] {
        None, ReadOnly, Hidden, System, Directory, Archive, Device, Normal, Temporary,
        SparseFile, ReparsePoint, Compressed, Offline, NotContentIndexed, Encrypted,
        IntegrityStream, NoScrubData
    };

    private FileAttributes(String name, int ordinal, int value) {
        this.__cs2jName = name;
        this.__cs2jOrdinal = ordinal;
        this.__cs2jValue = value;
    }

    public int getValue() {
        return this.__cs2jValue;
    }

    public int ordinal() {
        return this.__cs2jOrdinal;
    }

    public String name() {
        return this.__cs2jName;
    }

    @Override
    public String toString() {
        return this.__cs2jName;
    }

    public static FileAttributes[] values() {
        return __cs2jValues.clone();
    }

    public static FileAttributes valueOf(String name) {
        for (FileAttributes candidate : __cs2jValues) {
            if (candidate.__cs2jName.equals(name)) {
                return candidate;
            }
        }
        throw new IllegalArgumentException("No enum constant FileAttributes." + name);
    }

    public static FileAttributes fromValue(int value) {
        for (FileAttributes candidate : __cs2jValues) {
            if (candidate.__cs2jValue == value) {
                return candidate;
            }
        }
        return new FileAttributes(java.lang.String.valueOf(value), -1, value);
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof FileAttributes && ((FileAttributes) other).__cs2jValue == this.__cs2jValue;
    }

    @Override
    public int hashCode() {
        return Integer.hashCode(this.__cs2jValue);
    }
}
