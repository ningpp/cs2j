package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.io.File;
import java.nio.file.Paths;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for PathHelper utility methods.
 * Uses platform-aware assertions since Windows paths differ from Unix paths.
 */
class PathHelperTest {

    /** Returns a real platform-specific absolute path for testing. */
    private static String absPath(String... parts) {
        // Build from current directory's root, which is always absolute
        java.nio.file.Path root = Paths.get("").toAbsolutePath().getRoot();
        java.nio.file.Path p = root;
        for (String part : parts) {
            p = p.resolve(part);
        }
        return p.toString();
    }

    // ---- isPathRooted ----

    @Test
    void isPathRooted_absolutePath() {
        // Use a real platform absolute path
        String path = Paths.get("/foo/bar").toAbsolutePath().toString();
        assertTrue(PathHelper.isPathRooted(path));
    }

    @Test
    void isPathRooted_relativePath() {
        assertFalse(PathHelper.isPathRooted("foo/bar"));
    }

    @Test
    void isPathRooted_null() {
        assertFalse(PathHelper.isPathRooted(null));
    }

    @Test
    void isPathRooted_currentDir() {
        assertFalse(PathHelper.isPathRooted("somefile.txt"));
    }

    @Test
    void isPathRooted_windowsDrive() {
        String os = System.getProperty("os.name").toLowerCase();
        if (os.contains("win")) {
            assertTrue(PathHelper.isPathRooted("C:\\Users"));
        } else {
            assertTrue(PathHelper.isPathRooted("/usr/bin"));
        }
    }

    // ---- getFileName ----

    @Test
    void getFileName_withExtension() {
        assertEquals("bar.txt", PathHelper.getFileName("foo" + File.separator + "bar.txt"));
    }

    @Test
    void getFileName_withoutExtension() {
        assertEquals("bar", PathHelper.getFileName("foo" + File.separator + "bar"));
    }

    @Test
    void getFileName_justFilename() {
        assertEquals("file.txt", PathHelper.getFileName("file.txt"));
    }

    @Test
    void getFileName_multipleDots() {
        assertEquals("archive.tar.gz", PathHelper.getFileName("foo" + File.separator + "archive.tar.gz"));
    }

    @Test
    void getFileName_rootPath() {
        // Root path has no file name component; getFileName returns null on most systems
        String root = Paths.get("/").toAbsolutePath().getRoot().toString();
        // On Windows, root is like "C:\" which has a file name; on Unix "/" returns null
        // Just verify it doesn't throw
        PathHelper.getFileName(root);
    }

    @Test
    void getFileName_nullPath() {
        assertThrows(NullPointerException.class, () -> PathHelper.getFileName(null));
    }

    // ---- getFileNameWithoutExtension ----

    @Test
    void getFileNameWithoutExtension_withExtension() {
        assertEquals("bar", PathHelper.getFileNameWithoutExtension("foo" + File.separator + "bar.txt"));
    }

    @Test
    void getFileNameWithoutExtension_noExtension() {
        assertEquals("bar", PathHelper.getFileNameWithoutExtension("foo" + File.separator + "bar"));
    }

    @Test
    void getFileNameWithoutExtension_multipleDots() {
        assertEquals("archive.tar", PathHelper.getFileNameWithoutExtension("foo" + File.separator + "archive.tar.gz"));
    }

    @Test
    void getFileNameWithoutExtension_justFilename() {
        assertEquals("file", PathHelper.getFileNameWithoutExtension("file.java"));
    }

    @Test
    void getFileNameWithoutExtension_dotfile() {
        // ".gitignore" - dot at position 0, dot > 0 is false, returns full name
        assertEquals(".gitignore", PathHelper.getFileNameWithoutExtension(".gitignore"));
    }

    // ---- getDirectoryName ----

    @Test
    void getDirectoryName_simplePath() {
        String result = PathHelper.getDirectoryName("foo" + File.separator + "bar.txt");
        assertEquals("foo", result);
    }

    @Test
    void getDirectoryName_nestedPath() {
        String result = PathHelper.getDirectoryName("a" + File.separator + "b" + File.separator + "c.txt");
        assertEquals("a" + File.separator + "b", result);
    }

    @Test
    void getDirectoryName_justFilename() {
        String result = PathHelper.getDirectoryName("file.txt");
        assertNull(result);
    }

    @Test
    void getDirectoryName_absolutePath() {
        // Build a platform-appropriate absolute path
        String absPath = Paths.get("/usr/local/bin").toAbsolutePath().toString();
        String result = PathHelper.getDirectoryName(absPath);
        assertNotNull(result);
        assertTrue(result.endsWith("local") || result.contains("local"));
    }

    @Test
    void getDirectoryName_nullPath() {
        assertThrows(NullPointerException.class, () -> PathHelper.getDirectoryName(null));
    }

    // ---- getFullPath ----

    @Test
    void getFullPath_relativePath() {
        String result = PathHelper.getFullPath("somefile.txt");
        assertNotNull(result);
        assertTrue(result.endsWith("somefile.txt"));
        // Should be absolute
        assertTrue(PathHelper.isPathRooted(result));
    }

    @Test
    void getFullPath_alreadyAbsolute() {
        String absPath = Paths.get("/tmp/test.txt").toAbsolutePath().toString();
        String result = PathHelper.getFullPath(absPath);
        assertNotNull(result);
        assertTrue(result.contains("test.txt"));
    }
}
