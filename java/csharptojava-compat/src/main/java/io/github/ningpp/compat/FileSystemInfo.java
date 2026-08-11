package io.github.ningpp.compat;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
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

    public void delete() {
        try {
            Files.deleteIfExists(path);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
