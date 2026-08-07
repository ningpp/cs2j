import { describe, expect, it } from "vitest";
import {
    ArgumentException,
    ArgumentNullException,
    ArgumentOutOfRangeException,
    ArithmeticException,
    CsException,
    DivideByZeroException,
    FormatException,
    IndexOutOfRangeException,
    InvalidOperationException,
    IOException,
    KeyNotFoundException,
    NotImplementedException,
    NotSupportedException,
    OverflowException,
} from "./CsException.js";

describe("CsException hierarchy (spec §4.3.9)", () => {
    it("every class is an Error and carries the runtime type name", () => {
        for (const ctor of [
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
        ]) {
            const ex = new ctor("boom");
            expect(ex).toBeInstanceOf(Error);
            expect(ex).toBeInstanceOf(CsException);
            expect(ex.name).toBe(ctor.name);
            expect(ex.Message).toBe("boom");
        }
    });

    it("argument exceptions chain through ArgumentException", () => {
        expect(new ArgumentNullException("x")).toBeInstanceOf(ArgumentException);
        expect(new ArgumentOutOfRangeException("x")).toBeInstanceOf(ArgumentException);
        expect(new ArgumentException("x")).toBeInstanceOf(CsException);
    });

    it("arithmetic exceptions chain through ArithmeticException", () => {
        expect(new DivideByZeroException()).toBeInstanceOf(ArithmeticException);
        expect(new OverflowException()).toBeInstanceOf(ArithmeticException);
    });

    it("InnerException is null by default and settable via constructor", () => {
        expect(new CsException("outer").InnerException).toBeNull();
        const inner = new CsException("inner");
        const outer = new CsException("outer", inner);
        expect(outer.InnerException).toBe(inner);
        expect(outer.InnerException?.Message).toBe("inner");
    });

    it("StackTrace is populated when thrown/created in V8", () => {
        const ex = new ArgumentException("trace");
        expect(typeof ex.StackTrace).toBe("string");
        expect(ex.StackTrace?.length).toBeGreaterThan(0);
    });

    it("ToString includes the type name and message", () => {
        const ex = new FormatException("bad format");
        expect(ex.ToString()).toContain("FormatException");
        expect(ex.ToString()).toContain("bad format");
        expect(`${ex}`).toContain("FormatException");
    });

    it("KeyNotFoundException/IOException/others are direct CsException subclasses", () => {
        expect(new KeyNotFoundException("k")).toBeInstanceOf(CsException);
        expect(new IOException("io")).toBeInstanceOf(CsException);
        expect(new NotSupportedException()).toBeInstanceOf(CsException);
        expect(new NotImplementedException()).toBeInstanceOf(CsException);
        expect(new InvalidOperationException()).toBeInstanceOf(CsException);
        expect(new IndexOutOfRangeException()).toBeInstanceOf(CsException);
    });
});
