package io.github.ningpp.compat;

/**
 * Compat for System.IO.FileAttributes (flags). Stored as int per the
 * converter's flags-class pattern.
 */
public final class FileAttributes {
    public static final int ReadOnly = 1;
    public static final int Hidden = 2;
    public static final int System = 4;
    public static final int Directory = 16;
    public static final int Archive = 32;
    public static final int Device = 64;
    public static final int Normal = 128;
    public static final int Temporary = 256;
    public static final int SparseFile = 512;
    public static final int ReparsePoint = 1024;
    public static final int Compressed = 2048;
    public static final int Offline = 4096;
    public static final int NotContentIndexed = 8192;
    public static final int Encrypted = 16384;
    public static final int IntegrityStream = 32768;
    public static final int NoScrubData = 131072;

    private FileAttributes() {
    }
}
