import { describe, expect, it } from "vitest";
import { CsQueue } from "./CsQueue.js";

describe("CsQueue", () => {
    it("enqueue/dequeue is FIFO", () => {
        const q = new CsQueue<number>();
        q.enqueue(1);
        q.enqueue(2);
        q.enqueue(3);
        expect(q.dequeue()).toBe(1);
        expect(q.dequeue()).toBe(2);
        expect(q.size()).toBe(1);
        expect(q.peek()).toBe(3);
        expect(q.dequeue()).toBe(3);
    });

    it("poll returns null when empty", () => {
        const q = new CsQueue<number>();
        expect(q.poll()).toBeNull();
        q.enqueue(5);
        expect(q.poll()).toBe(5);
        expect(q.poll()).toBeNull();
    });

    it("add is an alias of enqueue", () => {
        const q = new CsQueue<number>();
        q.add(9);
        expect(q.dequeue()).toBe(9);
    });

    it("constructor accepts an iterable", () => {
        const q = new CsQueue<number>([1, 2]);
        expect(q.size()).toBe(2);
        expect(q.dequeue()).toBe(1);
    });

    it("is iterable in FIFO order", () => {
        const q = new CsQueue<number>();
        q.enqueue(4);
        q.enqueue(5);
        expect([...q]).toEqual([4, 5]);
    });

    it("clear empties the queue", () => {
        const q = new CsQueue<number>([1, 2, 3]);
        q.clear();
        expect(q.isEmpty()).toBe(true);
        expect(q.size()).toBe(0);
    });

    it("dequeue on empty throws", () => {
        expect(() => new CsQueue<number>().dequeue()).toThrow();
    });
});
