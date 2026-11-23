import { afterEach, describe, expect, it, vi } from "vitest";
import { loadIssues, sendFeedback } from "../src/api";

afterEach(() => vi.unstubAllGlobals());

describe("GitHub feedback client", () => {
  it("can identify submissions over plain LAN HTTP without randomUUID", async () => {
    vi.stubGlobal("crypto", { getRandomValues: (bytes: Uint8Array) => bytes.fill(123) });
    const fetch = vi.fn(async (_url, init) => {
      expect(JSON.parse(init.body).submissionId).toMatch(/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/);
      return Response.json({ url: "https://github.com/abossard/sattle/issues/144", id: 144, action: "comment" });
    });
    vi.stubGlobal("fetch", fetch);
    await sendFeedback("key", { findingId: "plain-http" });
  });

  it("reuses the submission identity after an uncertain network failure", async () => {
    const bodies: Record<string, unknown>[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url, init) => {
      bodies.push(JSON.parse(init.body));
      if (bodies.length === 1) throw new Error("Response lost after GitHub accepted it");
      return Response.json({ url: "https://github.com/abossard/sattle/issues/144#issuecomment-1", id: 1, action: "comment" });
    }));
    const payload = { findingId: "test-retry", rationale: "Keep one identity per answer" };
    await expect(sendFeedback("key", payload)).rejects.toThrow("Response lost");
    await sendFeedback("key", payload);
    expect(bodies[0].submissionId).toMatch(/^[a-f0-9-]{36}$/);
    expect(bodies[1].submissionId).toBe(bodies[0].submissionId);
  });

  it.each([51, 125])("loads %i issue owners without exceeding the API batch limit", async (count) => {
    const fetch = vi.fn(async (url) => {
      const numbers = new URL(url, "http://localhost").searchParams.get("numbers")!.split(",").map(Number);
      expect(numbers.length).toBeLessThanOrEqual(50);
      return Response.json({ issues: numbers.map((number) => ({ number, title: `Issue ${number}`, state: "open" })) });
    });
    vi.stubGlobal("fetch", fetch);
    const issues = await loadIssues(Array.from({ length: count }, (_, index) => index + 1));
    expect(issues.size).toBe(count);
  });
});
