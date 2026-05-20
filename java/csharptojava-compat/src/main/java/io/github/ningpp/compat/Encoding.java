package io.github.ningpp.compat;

import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;

/** Small System.Text.Encoding facade over java.nio.charset.Charset. */
public final class Encoding {
    private final Charset charset;
    private final int codePage;

    private Encoding(Charset charset, int codePage) {
        this.charset = charset;
        this.codePage = codePage;
    }

    public static Encoding getEncoding(String name) {
        Charset charset = Charset.forName(name);
        return new Encoding(charset, codePageFor(charset));
    }

    public static Encoding getEncoding(int codePage) {
        return switch (codePage) {
            case 1200 -> new Encoding(Charset.forName("UTF-16LE"), codePage);
            case 1201 -> new Encoding(Charset.forName("UTF-16BE"), codePage);
            case 20127 -> new Encoding(StandardCharsets.US_ASCII, codePage);
            case 65001 -> new Encoding(StandardCharsets.UTF_8, codePage);
            default -> getEncoding("windows-" + codePage);
        };
    }

    public static Encoding getEncoding() {
        return getDefault();
    }

    public static Encoding getASCII() {
        return new Encoding(StandardCharsets.US_ASCII, 20127);
    }

    public static Encoding getUTF8() {
        return new Encoding(StandardCharsets.UTF_8, 65001);
    }

    public static Encoding getDefault() {
        Charset charset = Charset.defaultCharset();
        return new Encoding(charset, codePageFor(charset));
    }

    public Charset toCharset() {
        return charset;
    }

    public String getBodyName() {
        return charset.name().toLowerCase(java.util.Locale.ROOT);
    }

    public int getCodePage() {
        return codePage;
    }

    private static int codePageFor(Charset charset) {
        String name = charset.name().toUpperCase(java.util.Locale.ROOT);
        if ("UTF-8".equals(name)) return 65001;
        if ("UTF-16LE".equals(name)) return 1200;
        if ("UTF-16BE".equals(name)) return 1201;
        if ("US-ASCII".equals(name)) return 20127;
        if (name.startsWith("WINDOWS-")) {
            try {
                return Integer.parseInt(name.substring("WINDOWS-".length()));
            } catch (NumberFormatException ignored) {
                return 0;
            }
        }
        return 0;
    }
}
