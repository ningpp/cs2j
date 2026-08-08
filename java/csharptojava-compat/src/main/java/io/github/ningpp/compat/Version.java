package io.github.ningpp.compat;

/**
 * Compat value type for System.Version.
 */
public final class Version {
    private final int major;
    private final int minor;
    private final int build;
    private final int revision;

    public Version(int major) {
        this(major, 0, 0, -1);
    }

    public Version(int major, int minor) {
        this(major, minor, 0, -1);
    }

    public Version(int major, int minor, int build) {
        this(major, minor, build, -1);
    }

    public Version(int major, int minor, int build, int revision) {
        this.major = major;
        this.minor = minor;
        this.build = build;
        this.revision = revision;
    }

    public int getMajor() {
        return major;
    }

    public int getMinor() {
        return minor;
    }

    public int getBuild() {
        return build;
    }

    public int getRevision() {
        return revision;
    }

    public int getMajorRevision() {
        return revision < 0 ? -1 : (revision >> 16) & 0xFFFF;
    }

    public int getMinorRevision() {
        return revision < 0 ? -1 : revision & 0xFFFF;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof Version)) return false;
        Version other = (Version) obj;
        return major == other.major
            && minor == other.minor
            && build == other.build
            && revision == other.revision;
    }

    @Override
    public int hashCode() {
        int result = major;
        result = 31 * result + minor;
        result = 31 * result + build;
        result = 31 * result + revision;
        return result;
    }

    @Override
    public String toString() {
        if (revision < 0) {
            return build < 0 ? major + "." + minor : major + "." + minor + "." + build;
        }
        return major + "." + minor + "." + build + "." + revision;
    }
}
