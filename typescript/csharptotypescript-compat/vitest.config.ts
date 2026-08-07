import { defineConfig } from "vitest/config";

// 兼容库语义测试（spec §5.6）：与 src 同目录的 *.test.ts，vitest 独立于 tsc 构建。
export default defineConfig({
  test: {
    include: ["src/**/*.test.ts"],
  },
});
