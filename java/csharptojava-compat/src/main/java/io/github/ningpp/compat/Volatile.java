package io.github.ningpp.compat;

import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Compat bridge for {@code System.Threading.Volatile.Read/Write}.
 *
 * <p>C29 诚实化半区：C# 的 {@code Volatile.Read(ref x)} 是 acquire 读、
 * {@code Volatile.Write(ref x, v)} 是 release 写。转换器经 ref 归一会把实参落为
 * 各类 {@code *Holder}（或 {@link ObjectHolder}）单元，本桥对 holder 的公共
 * {@code value} 字段取 {@link VarHandle}，用 {@code getAcquire}/{@code setRelease}
 * 访问模式提供真实的 acquire/release 语义——不是普通字段读写，也不依赖
 * {@code sun.misc.Unsafe}。</p>
 *
 * <p>已知边界（与 CS2J2960 站点挂账一致）：指针形 {@code Volatile.Read(ref void*)}
 * 等 unsafe 重载不提供；若生成代码出现非 holder 传参形态，会在调用点暴露为
 * 编译/运行期错误，而不是静默降级为普通读写。</p>
 */
public final class Volatile {

    private static final Map<Class<?>, VarHandle> HOLDERS = new ConcurrentHashMap<>();

    private Volatile() {
    }

    public static boolean read(BoolHolder location) {
        return (boolean) handle(location).getAcquire(location);
    }

    public static void write(BoolHolder location, boolean value) {
        handle(location).setRelease(location, value);
    }

    public static byte read(ByteHolder location) {
        return (byte) handle(location).getAcquire(location);
    }

    public static void write(ByteHolder location, byte value) {
        handle(location).setRelease(location, value);
    }

    public static short read(ShortHolder location) {
        return (short) handle(location).getAcquire(location);
    }

    public static void write(ShortHolder location, short value) {
        handle(location).setRelease(location, value);
    }

    public static char read(CharHolder location) {
        return (char) handle(location).getAcquire(location);
    }

    public static void write(CharHolder location, char value) {
        handle(location).setRelease(location, value);
    }

    public static int read(IntHolder location) {
        return (int) handle(location).getAcquire(location);
    }

    public static void write(IntHolder location, int value) {
        handle(location).setRelease(location, value);
    }

    public static long read(LongHolder location) {
        return (long) handle(location).getAcquire(location);
    }

    public static void write(LongHolder location, long value) {
        handle(location).setRelease(location, value);
    }

    public static float read(FloatHolder location) {
        return (float) handle(location).getAcquire(location);
    }

    public static void write(FloatHolder location, float value) {
        handle(location).setRelease(location, value);
    }

    public static double read(DoubleHolder location) {
        return (double) handle(location).getAcquire(location);
    }

    public static void write(DoubleHolder location, double value) {
        handle(location).setRelease(location, value);
    }

    public static <T> T read(ObjectHolder<T> location) {
        @SuppressWarnings("unchecked")
        T result = (T) handle(location).getAcquire(location);
        return result;
    }

    public static <T> void write(ObjectHolder<T> location, T value) {
        handle(location).setRelease(location, value);
    }

    private static VarHandle handle(Object location) {
        Objects.requireNonNull(location, "location");
        Class<?> holderClass = location.getClass();
        return HOLDERS.computeIfAbsent(holderClass, Volatile::createHandle);
    }

    @SuppressWarnings({ "rawtypes", "unchecked" })
    private static VarHandle createHandle(Class<?> holderClass) {
        try {
            Class fieldType = holderClass.getDeclaredField("value").getType();
            return MethodHandles.lookup().findVarHandle((Class) holderClass, "value", fieldType);
        } catch (NoSuchFieldException | IllegalAccessException e) {
            throw new IllegalStateException(
                "C29 Volatile bridge expects a public 'value' field on "
                + holderClass.getName(), e);
        }
    }
}
