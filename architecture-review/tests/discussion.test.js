import { afterEach, describe, expect, it, vi } from "vitest";
import { onRequestGet } from "../functions/api/discussion.js";

afterEach(() => vi.unstubAllGlobals());

describe("live GitHub discussion", () => {
  it.each([0, 7, 101, 205])("serves the latest discussion from an issue with %i comments", async (count) => {
    const fetch = vi.fn(async (url) => {
      if (!String(url).includes("/comments?")) return Response.json({
        number: 144, title: "Keep domain policy in projections", state: "open", body: "Owner decision", comments: count, html_url: "https://github.com/abossard/sattle/issues/144",
      });
      const page = Number(new URL(url).searchParams.get("page") || 1);
      const start = (page - 1) * 100;
      return Response.json(Array.from({ length: Math.max(0, Math.min(100, count - start)) }, (_, index) => ({
        id: start + index + 1, body: `Answer ${start + index + 1}`, user: { login: "reviewer" },
        html_url: `https://github.com/abossard/sattle/issues/144#issuecomment-${start + index + 1}`, created_at: "2026-11-23T21:00:00Z",
      })));
    });
    vi.stubGlobal("fetch", fetch);
    const response = await onRequestGet({ request: new Request("http://localhost/api/discussion?number=144"), env: {} });
    expect(response.status).toBe(200);
    const discussion = await response.json();
    expect(discussion.issue.body).toBe("Owner decision");
    expect(discussion.totalComments).toBe(count);
    expect(discussion.comments).toHaveLength(Math.min(20, count));
    if (count) expect(discussion.comments.at(-1).id).toBe(count);
    expect(response.headers.get("Cache-Control")).toBe("no-store");
  });
});
