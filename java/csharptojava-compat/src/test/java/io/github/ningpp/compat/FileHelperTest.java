package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import java.io.IOException;
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
}
