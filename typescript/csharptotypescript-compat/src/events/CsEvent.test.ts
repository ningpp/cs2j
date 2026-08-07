import { describe, expect, it } from "vitest";
import { CsEvent } from "./CsEvent.js";

describe("CsEvent (spec §4.3.7)", () => {
    it("Add registers handlers; Invoke calls them in order with args", () => {
        const evt = new CsEvent<(sender: unknown, args: { n: number }) => void>();
        const calls: number[] = [];
        const h1 = (_s: unknown, a: { n: number }) => calls.push(a.n);
        const h2 = (_s: unknown, a: { n: number }) => calls.push(a.n * 10);
        evt.Add(h1);
        evt.Add(h2);
        evt.Invoke(null, { n: 3 });
        expect(calls).toEqual([3, 30]);
    });

    it("duplicate handlers are invoked multiple times (no dedup)", () => {
        const evt = new CsEvent<() => void>();
        let count = 0;
        const handler = () => count++;
        evt.Add(handler);
        evt.Add(handler);
        evt.Invoke();
        expect(count).toBe(2);
        expect(evt.GetInvocationList()).toHaveLength(2);
    });

    it("Invoke on an empty event is safe", () => {
        const evt = new CsEvent<() => void>();
        expect(() => evt.Invoke()).not.toThrow();
    });

    it("Remove removes the last matching handler", () => {
        const evt = new CsEvent<() => void>();
        const calls: string[] = [];
        const h1 = () => calls.push("a");
        const h2 = () => calls.push("b");
        evt.Add(h1);
        evt.Add(h2);
        evt.Add(h1);
        evt.Remove(h1);
        evt.Invoke();
        expect(calls).toEqual(["a", "b"]);
    });

    it("Remove of an unregistered handler is a no-op", () => {
        const evt = new CsEvent<() => void>();
        evt.Add(() => {});
        evt.Remove(() => {});
        expect(evt.GetInvocationList()).toHaveLength(1);
    });

    it("GetInvocationList returns a copy", () => {
        const evt = new CsEvent<() => void>();
        const handler = () => {};
        evt.Add(handler);
        const list = evt.GetInvocationList();
        list.length = 0;
        expect(evt.GetInvocationList()).toHaveLength(1);
    });

    it("a throwing handler stops later handlers (C# multicast semantics)", () => {
        const evt = new CsEvent<() => void>();
        const calls: string[] = [];
        evt.Add(() => {
            calls.push("first");
            throw new Error("boom");
        });
        evt.Add(() => calls.push("second"));
        expect(() => evt.Invoke()).toThrow("boom");
        expect(calls).toEqual(["first"]);
    });

    it("handlers added during Invoke do not run in the current batch (snapshot)", () => {
        const evt = new CsEvent<() => void>();
        const calls: string[] = [];
        const late = () => calls.push("late");
        evt.Add(() => {
            calls.push("early");
            evt.Add(late);
        });
        evt.Invoke();
        expect(calls).toEqual(["early"]);
        evt.Invoke();
        expect(calls).toEqual(["early", "early", "late"]);
    });
});
