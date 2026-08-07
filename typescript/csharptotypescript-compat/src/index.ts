/**
 * @csharptotypescript/compat — 兼容运行时库（spec §4.3）。
 * 桥接 C# 与 TS/JS 语义差异，零第三方运行时依赖；组件按任务增量导出：
 * core/（Task 29）、time/（Task 30）、text/（Task 31）、collections/（Task 32）、
 * events/（Task 33）、exceptions/（Task 34）、primitives/（Task 35）。
 */
export { type Ref, refOf, outRef } from "./core/Ref.js";
export { CsObject } from "./core/CsObject.js";
export { CsRecord } from "./core/CsRecord.js";
export { CsRuntime } from "./core/CsRuntime.js";
export { TypeToken } from "./core/TypeToken.js";
export { csDefault } from "./core/csDefault.js";
export { cloneStruct } from "./core/cloneStruct.js";
export { CsDateTime } from "./time/CsDateTime.js";
export { CsTimeSpan } from "./time/CsTimeSpan.js";
export { DayOfWeek } from "./time/DayOfWeek.js";
export { CsString } from "./text/CsString.js";
export { CsStringBuilder } from "./text/CsStringBuilder.js";
export { StringSplitOptions } from "./text/StringSplitOptions.js";
export { CsKeyValuePair } from "./collections/CsKeyValuePair.js";
export { type IEqualityComparer, CsDictionary } from "./collections/CsDictionary.js";
export { CsSet } from "./collections/CsSet.js";
export { CsEvent } from "./events/CsEvent.js";
export { CsDecimal } from "./primitives/CsDecimal.js";
export { CsChar } from "./primitives/CsChar.js";
export { CsGuid } from "./primitives/CsGuid.js";
export {
    CsException,
    ArgumentException,
    ArgumentNullException,
    ArgumentOutOfRangeException,
    InvalidOperationException,
    NotSupportedException,
    NotImplementedException,
    IndexOutOfRangeException,
    KeyNotFoundException,
    ArithmeticException,
    DivideByZeroException,
    OverflowException,
    FormatException,
    IOException,
} from "./exceptions/CsException.js";
