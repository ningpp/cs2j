package io.github.ningpp.compat;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * 读取 big-literal-resource 预处理外置的载荷。
 *
 * <p>本类是 {@code Cs2jPrelude.Cs2jResource} 的 Java 手写对应物（经
 * config/TypeMappings.json 映射），方法名与 C# 侧一一对应，载荷格式逐字节一致：
 * 定长元素数组使用小端定宽槽位表（bool 1 字节 0/1，char 为 UTF-16 码元 2 字节），
 * {@code string[]} 使用「int32 小端字节数 + UTF-8 字节」逐项串联，单个 {@code string}
 * 的整份文件即 UTF-8 字节。</p>
 *
 * <p>C# 侧资源以 EmbeddedResource + LogicalName 编入宿主程序集，运行时按
 * LogicalName 查清单流；Java 侧对应 classpath 资源，因此逻辑名必须一致
 * （{@code cs2j/<Owner>.<Member>.<hash>.<ext>}）。</p>
 */
public final class Cs2jResource {

    private Cs2jResource() {
    }

    /** 读取 {@code byte[]} 载荷。 */
    public static byte[] loadBytes(String name) {
        return ReadAll(name);
    }

    /** 读取 {@code short[]} 载荷。 */
    public static short[] loadInt16(String name) {
        byte[] bytes = ReadAll(name);
        short[] values = new short[bytes.length / 2];
        for (int i = 0; i < values.length; i++) {
            values[i] = (short) ((bytes[i * 2] & 0xFF) | ((bytes[i * 2 + 1] & 0xFF) << 8));
        }
        return values;
    }

    /** 读取 {@code int[]} 载荷。 */
    public static int[] loadInt32(String name) {
        byte[] bytes = ReadAll(name);
        int[] values = new int[bytes.length / 4];
        for (int i = 0; i < values.length; i++) {
            int offset = i * 4;
            values[i] = (bytes[offset] & 0xFF)
                    | ((bytes[offset + 1] & 0xFF) << 8)
                    | ((bytes[offset + 2] & 0xFF) << 16)
                    | ((bytes[offset + 3] & 0xFF) << 24);
        }
        return values;
    }

    /** 读取 {@code long[]} 载荷。 */
    public static long[] loadInt64(String name) {
        byte[] bytes = ReadAll(name);
        long[] values = new long[bytes.length / 8];
        for (int i = 0; i < values.length; i++) {
            int offset = i * 8;
            long bits = 0L;
            for (int b = 0; b < 8; b++) {
                bits |= (long) (bytes[offset + b] & 0xFF) << (8 * b);
            }
            values[i] = bits;
        }
        return values;
    }

    /** 读取 {@code float[]} 载荷。 */
    public static float[] loadSingle(String name) {
        int[] bits = loadInt32(name);
        float[] values = new float[bits.length];
        for (int i = 0; i < values.length; i++) {
            values[i] = Float.intBitsToFloat(bits[i]);
        }
        return values;
    }

    /** 读取 {@code double[]} 载荷。 */
    public static double[] loadDouble(String name) {
        long[] bits = loadInt64(name);
        double[] values = new double[bits.length];
        for (int i = 0; i < values.length; i++) {
            values[i] = Double.longBitsToDouble(bits[i]);
        }
        return values;
    }

    /** 读取 {@code char[]} 载荷（UTF-16 码元，含未配对代理项）。 */
    public static char[] loadChars(String name) {
        byte[] bytes = ReadAll(name);
        char[] values = new char[bytes.length / 2];
        for (int i = 0; i < values.length; i++) {
            values[i] = (char) ((bytes[i * 2] & 0xFF) | ((bytes[i * 2 + 1] & 0xFF) << 8));
        }
        return values;
    }

    /** 读取 {@code boolean[]} 载荷。 */
    public static boolean[] loadBooleans(String name) {
        byte[] bytes = ReadAll(name);
        boolean[] values = new boolean[bytes.length];
        for (int i = 0; i < values.length; i++) {
            values[i] = bytes[i] != 0;
        }
        return values;
    }

    /** 读取 {@code String[]} 载荷。 */
    public static String[] loadStrings(String name) {
        byte[] bytes = ReadAll(name);
        java.util.ArrayList<String> values = new java.util.ArrayList<String>();
        int offset = 0;
        while (offset < bytes.length) {
            int length = (bytes[offset] & 0xFF)
                    | ((bytes[offset + 1] & 0xFF) << 8)
                    | ((bytes[offset + 2] & 0xFF) << 16)
                    | ((bytes[offset + 3] & 0xFF) << 24);
            offset += 4;
            values.add(new String(bytes, offset, length, StandardCharsets.UTF_8));
            offset += length;
        }
        return values.toArray(new String[0]);
    }

    /** 读取单个 {@code String} 载荷。 */
    public static String loadString(String name) {
        return new String(ReadAll(name), StandardCharsets.UTF_8);
    }

    private static byte[] ReadAll(String name) {
        if (name == null) {
            throw new NullPointerException("name");
        }

        String path = name.startsWith("/") ? name : "/" + name;
        InputStream stream = Cs2jResource.class.getResourceAsStream(path);
        if (stream == null) {
            ClassLoader loader = Thread.currentThread().getContextClassLoader();
            if (loader != null) {
                stream = loader.getResourceAsStream(name);
            }
        }

        if (stream == null) {
            throw new IllegalStateException(
                    "cs2j resource '" + name + "' was not found on the classpath. "
                    + "big-literal-resource embeds it as an EmbeddedResource whose LogicalName "
                    + "becomes src/main/resources/<name>; a missing file means the resource "
                    + "item was lost during conversion.");
        }

        try {
            ByteArrayOutputStream buffer = new ByteArrayOutputStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.read(chunk)) > 0) {
                buffer.write(chunk, 0, read);
            }
            return buffer.toByteArray();
        } catch (IOException exception) {
            throw new IllegalStateException(
                    "cs2j resource '" + name + "' could not be read", exception);
        } finally {
            try {
                stream.close();
            } catch (IOException ignored) {
                // Nothing useful to do on close failure of a classpath stream.
            }
        }
    }
}
