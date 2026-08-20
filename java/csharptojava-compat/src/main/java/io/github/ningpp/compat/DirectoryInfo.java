package io.github.ningpp.compat;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;

public class DirectoryInfo {
    private final Path path;

    public DirectoryInfo(String path) {
        this.path = Paths.get(path);
    }

    public String getFullName() {
        return path.toAbsolutePath().normalize().toString();
    }

    public DirectoryInfo getParent() {
        Path parent = path.getParent();
        return parent == null ? null : new DirectoryInfo(parent.toString());
    }

    public static boolean exists(String path) {
        return Files.exists(Paths.get(path));
    }

    public FileSystemInfo[] getFiles() {
        ArrayList<FileSystemInfo> result = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(path)) {
            for (Path child : stream) {
                if (Files.isRegularFile(child)) {
                    result.add(new FileSystemInfo(child));
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(FileSystemInfo[]::new);
    }

    public DirectoryInfo[] getDirectories() {
        ArrayList<DirectoryInfo> result = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(path)) {
            for (Path child : stream) {
                if (Files.isDirectory(child)) {
                    result.add(new DirectoryInfo(child.toString()));
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(DirectoryInfo[]::new);
    }

    public void delete(boolean recursive) {
        try {
            if (recursive) {
                deleteRecursively(path);
            } else {
                Files.deleteIfExists(path);
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    public FileSystemInfo[] getFileSystemInfos(String searchPattern) {
        String glob = searchPattern == null || searchPattern.isEmpty() ? "*" : searchPattern;
        ArrayList<FileSystemInfo> result = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(path, glob)) {
            for (Path child : stream) {
                result.add(new FileSystemInfo(child));
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(FileSystemInfo[]::new);
    }

    public static String[] getDirectories(String directory) {
        ArrayList<String> result = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(Paths.get(directory))) {
            for (Path child : stream) {
                if (Files.isDirectory(child)) {
                    result.add(child.toString());
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(String[]::new);
    }

    public static String[] getDirectories(String directory, String searchPattern) {
        ArrayList<String> result = new ArrayList<>();
        String glob = searchPattern == null || searchPattern.isEmpty() ? "*" : searchPattern;
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(Paths.get(directory), glob)) {
            for (Path child : stream) {
                if (Files.isDirectory(child)) {
                    result.add(child.toString());
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(String[]::new);
    }

    public static String[] getFiles(String directory, String searchPattern) {
        ArrayList<String> result = new ArrayList<>();
        String glob = searchPattern == null || searchPattern.isEmpty() ? "*" : searchPattern;
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(Paths.get(directory), glob)) {
            for (Path child : stream) {
                if (Files.isRegularFile(child)) {
                    result.add(child.toString());
                }
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(String[]::new);
    }

    public static String[] getFiles(String directory, String searchPattern, boolean recursive) {
        if (!recursive) return getFiles(directory, searchPattern);
        ArrayList<String> result = new ArrayList<>();
        String glob = searchPattern == null || searchPattern.isEmpty() ? "*" : searchPattern;
        try (var stream = Files.walk(Paths.get(directory))) {
            stream.filter(Files::isRegularFile)
                .filter(path -> path.getFileName().toString().matches(globToRegex(glob)))
                .forEach(path -> result.add(path.toString()));
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return result.toArray(String[]::new);
    }

    private static String globToRegex(String glob) {
        return glob.replace(".", "\\.").replace("*", ".*").replace("?", ".");
    }

    /** C# Directory.CreateDirectory(path). */
    public static void createDirectory(String path) {
        try {
            Files.createDirectories(Paths.get(path));
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    /** C# Directory.Delete(path, recursive). */
    public static void delete(String path, boolean recursive) {
        try {
            Path p = Paths.get(path);
            if (recursive) {
                deleteRecursively(p);
            } else {
                Files.deleteIfExists(p);
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    private static void deleteRecursively(Path p) throws IOException {
        if (!Files.exists(p)) return;
        if (Files.isDirectory(p) && !Files.isSymbolicLink(p)) {
            try (DirectoryStream<Path> stream = Files.newDirectoryStream(p)) {
                for (Path child : stream) {
                    deleteRecursively(child);
                }
            }
        }
        Files.deleteIfExists(p);
    }
}
