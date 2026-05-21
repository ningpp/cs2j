package io.github.ningpp.compat;

import java.nio.file.Path;

public class FileSystemInfo {
    protected final Path path;

    public FileSystemInfo(Path path) {
        this.path = path;
    }

    public String getFullName() {
        return path.toAbsolutePath().normalize().toString();
    }

    public String getName() {
        Path name = path.getFileName();
        return name == null ? getFullName() : name.toString();
    }
}
