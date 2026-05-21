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
}
