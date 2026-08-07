/**
 * CsException — C# 异常层次兼容（spec §4.3.9，全部类）。
 * 基类含 InnerException/Message/StackTrace；name 按运行时类型设置（new.target.name），
 * instanceof 链可用（catch 类型过滤 §2.5 由发射器生成 instanceof 链）。
 *
 * 登记简化：
 * - ArgumentNullException 构造参数按消息字符串处理（C# 内部拼 "Parameter 'x'" 文案）。
 * - ToString 输出 "类型名: 消息 + 堆栈"（C# 为全限定名 + 内部异常链）。
 */
export class CsException extends Error {
    readonly InnerException: CsException | null = null;

    /** C# 重载：() / (string message) / (string message, Exception inner)。 */
    constructor(message?: string | null, innerException?: CsException | null) {
        super(message ?? undefined);
        this.name = new.target.name;
        if (innerException !== null && innerException !== undefined) {
            this.InnerException = innerException;
        }
    }

    /** C# Exception.Message（Error.message 的 PascalCase 视图）。 */
    get Message(): string {
        return this.message;
    }

    /** C# Exception.StackTrace（Error.stack 的 PascalCase 视图；无堆栈时为 undefined）。 */
    get StackTrace(): string | undefined {
        return this.stack;
    }

    /** 对齐 C# Exception.ToString()：类型名: 消息 + 堆栈。 */
    ToString(): string {
        const head = this.message.length > 0 ? `${this.name}: ${this.message}` : this.name;
        return this.stack !== undefined ? `${head}\n${this.stack}` : head;
    }

    toString(): string {
        return this.ToString();
    }
}

export class ArgumentException extends CsException {}
export class ArgumentNullException extends ArgumentException {}
export class ArgumentOutOfRangeException extends ArgumentException {}
export class InvalidOperationException extends CsException {}
export class NotSupportedException extends CsException {}
export class NotImplementedException extends CsException {}
export class IndexOutOfRangeException extends CsException {}
export class KeyNotFoundException extends CsException {}
export class ArithmeticException extends CsException {}
export class DivideByZeroException extends ArithmeticException {}
export class OverflowException extends ArithmeticException {}
export class FormatException extends CsException {}
export class IOException extends CsException {}
