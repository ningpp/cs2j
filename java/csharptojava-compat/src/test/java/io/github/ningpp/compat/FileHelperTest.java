package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

class FileHelperTest {

    @Test
    void openText_ShouldSkipUtf8Bom() throws Exception {
        Path tmp = Files.createTempFile("bomtest", ".txt");
        try {
            try (OutputStream os = Files.newOutputStream(tmp)) {
                os.write(0xEF);
                os.write(0xBB);
                os.write(0xBF);
                os.write("<?xml version=\"1.0\"?>".getBytes(StandardCharsets.UTF_8));
            }

            try (TextReader reader = FileHelper.openText(tmp.toString())) {
                int first = reader.peek();
                assertEquals('<', (char) first,
                    "First character after BOM should be '<', got: " + (char) first + " (0x" + Integer.toHexString(first) + ")");
            }
        } finally {
            Files.deleteIfExists(tmp);
        }
    }

    @Test
    void openText_ShouldWorkWithoutBom() throws Exception {
        Path tmp = Files.createTempFile("nobomtest", ".txt");
        try {
            try (OutputStream os = Files.newOutputStream(tmp)) {
                os.write("hello world".getBytes(StandardCharsets.UTF_8));
            }

            try (TextReader reader = FileHelper.openText(tmp.toString())) {
                int first = reader.peek();
                assertEquals('h', (char) first,
                    "First character without BOM should be 'h', got: " + (char) first);
            }
        } finally {
            Files.deleteIfExists(tmp);
        }
    }

    @Test
    void openText_ShouldReadClasspathResourceByName() throws Exception {
        try (TextReader reader = FileHelper.openText("io/github/ningpp/compat/file-helper-classpath.txt")) {
            assertEquals("classpath payload", reader.readToEnd().trim());
        }
    }

    @Test
    void openRead_ShouldReadClasspathResourceByName() throws Exception {
        try (InputStream stream = FileHelper.openRead("io/github/ningpp/compat/file-helper-classpath.txt")) {
            assertEquals("classpath payload", new String(stream.readAllBytes(), StandardCharsets.UTF_8).trim());
        }
    }

    /** C# File.Delete(path) declares no checked exception and ignores missing files. */
    @Test
    void delete_HasNoCheckedException_AndIsNoOpForMissingFile() throws IOException {
        Path tmp = Files.createTempFile("delete-noop", ".txt");
        deleteWithoutCheckedException(tmp.toString());
        assertFalse(Files.exists(tmp));

        // Deleting an already-missing file must not throw (File.Delete semantics).
        deleteWithoutCheckedException(tmp.toString());
        deleteWithoutCheckedException("definitely-not-there.txt");
    }

    /** Compiles only when FileHelper.delete declares no checked exception. */
    private static void deleteWithoutCheckedException(String path) {
        FileHelper.delete(path);
    }
}
