# Float16 (半精度浮点) 在 Java/Valhalla 中的分析

---

## C# 的 Float16 支持

C# / .NET 7+ 引入了 `System.Half` 类型:

| 特性 | 值 |
|------|-----|
| 类型名 | `Half` |
| 大小 | 16-bit (2 bytes) |
| 标准 | IEEE 754 binary16 |
| 范围 | ±65504 |
| 精度 | ~3.3 位十进制数字 |
| 用途 | 机器学习、图形、压缩 |

```c#
Half h = (Half)3.14f;
Half h2 = h + (Half)1.0;
```

---

## Java 当前的 Float16 支持

### 现状
Java **没有**内置的 Float16 类型。只有:
- `float`: 32-bit IEEE 754 binary32
- `double`: 64-bit IEEE 754 binary64

### 半精度浮点的用途
1. **机器学习/AI**: 深度学习框架大量使用 float16 进行训练和推理
2. **图形处理**: GPU 着色器、纹理压缩
3. **数据压缩**: 减少内存带宽需求
4. **科学计算**: 某些精度要求不高的场景

---

## Valhalla 的 Float16 方案

### JEP 469 (Vector API) 的明确提及

JEP 469: Vector API (Eighth Incubator) 中明确提到:

> *"We may add support for vectors of IEEE floating-point binary16 values (float16) ... represent a float16 value as a **value object**, with optimized layout in arrays"*

> *"hardware instructions on vectors of float16 values"*

这表明:
1. Float16 将作为 **value object** 实现
2. 会有优化的数组布局
3. 会利用硬件 SIMD 指令

### Float16 的实现路径

```
当前: 无支持
  ↓
Phase 1: 作为 Vector API 的 incubator 模块
  ↓
Phase 2: 独立的 value class Float16
  ↓
Phase 3: 作为 primitive class (可泛型化)
```

### 预期的 Float16 实现

```java
// 假设的 Float16 value class 实现
value class Float16 {
    private final short bits;  // 16-bit 存储

    // 构造函数
    private Float16(short bits) {
        this.bits = bits;
    }

    // 从 float 创建
    public static Float16 of(float value) {
        // 转换逻辑
        return new Float16(floatToHalfBits(value));
    }

    // 从 double 创建
    public static Float16 of(double value) {
        return of((float) value);
    }

    // 转换回 float
    public float floatValue() {
        return halfBitsToFloat(bits);
    }

    // 转换回 double
    public double doubleValue() {
        return (double) floatValue();
    }

    // 算术运算
    public Float16 add(Float16 other) {
        return Float16.of(this.floatValue() + other.floatValue());
    }

    public Float16 subtract(Float16 other) {
        return Float16.of(this.floatValue() - other.floatValue());
    }

    public Float16 multiply(Float16 other) {
        return Float16.of(this.floatValue() * other.floatValue());
    }

    public Float16 divide(Float16 other) {
        return Float16.of(this.floatValue() / other.floatValue());
    }

    // 比较
    public int compareTo(Float16 other) {
        return Float.compare(this.floatValue(), other.floatValue());
    }

    // 特殊值
    public static final Float16 ZERO = Float16.of(0.0f);
    public static final Float16 ONE = Float16.of(1.0f);
    public static final Float16 NaN = Float16.of(Float.NaN);
    public static final Float16 POSITIVE_INFINITY = Float16.of(Float.POSITIVE_INFINITY);
    public static final Float16 NEGATIVE_INFINITY = Float16.of(Float.NEGATIVE_INFINITY);

    // 位操作辅助方法
    private static short floatToHalfBits(float f) {
        int floatBits = Float.floatToIntBits(f);
        int sign = (floatBits >> 16) & 0x8000;
        int exponent = ((floatBits >> 23) & 0xFF) - 127 + 15;
        int mantissa = (floatBits >> 13) & 0x3FF;

        if (exponent <= 0) {
            // 非规格化数或零
            if (exponent < -10) return (short) sign;
            mantissa = (mantissa | 0x400) >> (1 - exponent);
            return (short) (sign | mantissa);
        } else if (exponent == 0xFF - 127 + 15) {
            // 无穷大或 NaN
            return (short) (sign | 0x7C00 | (mantissa != 0 ? 1 : 0));
        } else if (exponent > 30) {
            // 溢出为无穷大
            return (short) (sign | 0x7C00);
        }
        return (short) (sign | (exponent << 10) | mantissa);
    }

    private static float halfBitsToFloat(short h) {
        int sign = (h >> 15) & 1;
        int exponent = (h >> 10) & 0x1F;
        int mantissa = h & 0x3FF;

        if (exponent == 0) {
            if (mantissa == 0) {
                // 零
                return sign == 0 ? 0.0f : -0.0f;
            }
            // 非规格化数
            while ((mantissa & 0x400) == 0) {
                mantissa <<= 1;
                exponent--;
            }
            exponent++;
            mantissa &= ~0x400;
        } else if (exponent == 31) {
            if (mantissa == 0) {
                // 无穷大
                return sign == 0 ? Float.POSITIVE_INFINITY : Float.NEGATIVE_INFINITY;
            }
            // NaN
            return Float.NaN;
        }

        return Float.intBitsToFloat(
            (sign << 31) |
            ((exponent + 112) << 23) |
            (mantissa << 13)
        );
    }
}
```

### 与 Vector API 的集成

```java
// 使用 Vector API 进行 float16 向量运算
// (假设有 Float16 类型后)
Vector<Float16> v1 = Float16Vector.fromArray(SPECIES, array1, 0);
Vector<Float16> v2 = Float16Vector.fromArray(SPECIES, array2, 0);
Vector<Float16> result = v1.add(v2);
result.intoArray(resultArray, 0);
```

---

## Float16 的硬件支持

### x86/x64
- **F16C 指令集** (2011+): `VCVTPH2PS`, `VCVTPS2PH`
- **AVX-512 FP16** (2021+): 完整的 FP16 SIMD 支持

### ARM
- **ARMv8.2-A FP16**: 原生半精度浮点支持
- **SVE/SVE2**: 可扩展向量扩展中的 FP16

### GPU
- **NVIDIA**: 原生 FP16 支持 (CUDA cores)
- **AMD**: 原生 FP16 支持
- **Intel**: 原生 FP16 支持

### Java 的 Vector API 路径
Java 的 Vector API (Panama 项目) 正在逐步支持更多 SIMD 操作，Float16 向量支持是其中的一部分。

---

## 时间线评估

| 特性 | 预计可用时间 | 状态 |
|------|-------------|------|
| Vector API Float16 支持 | Java 26-27 (估计) | 探索中 |
| 独立的 Float16 value class | Java 27+ (估计) | 依赖 Valhalla |
| Float16 作为 primitive class | Java 28+ (估计) | 远期 |
| 完整的 Float16 泛型支持 | Java 29+ (估计) | 远期 |

---

## 对 cs2j 转换器的建议

### 当前方案
```java
// C# Half → Java float (精度损失可接受)
// 或使用第三方库如 Apache Commons Math 的 Half
```

### 推荐映射

| C# 类型 | Java 当前方案 | Valhalla 后方案 |
|---------|-------------|----------------|
| `Half` | `float` | `Float16` (value class) |
| `Half[]` | `float[]` | `Float16[]` (扁平布局) |
| `List<Half>` | `List<Float>` | `List<Float16>` |

### 注释建议
```java
/**
 * C# original: Half temperature
 * Java mapping: float temperature (half-precision not available)
 * Note: Java will support Float16 as a value class in future Valhalla releases
 */
float temperature = 0.0f;
```

---

## 参考链接

- [JEP 469: Vector API (Eighth Incubator)](https://openjdk.org/jeps/469)
- [IEEE 754 Half-Precision Format](https://en.wikipedia.org/wiki/Half-precision_floating-point_format)
- [.NET Half Structure](https://learn.microsoft.com/en-us/dotnet/api/system.half)
