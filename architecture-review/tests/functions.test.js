import { afterEach, describe, expect, it, vi } from "vitest";
import { onRequestGet as getConfig } from "../functions/api/config.js";
import { onRequestPost as postFeedback } from "../functions/api/feedback.js";
import { onRequestGet as getIssues } from "../functions/api/issues.js";

afterEach(() => vi.unstubAllGlobals());

const env = {
  GITHUB_OWNER: "abossard",
  GITHUB_REPOSITORY: "sattle",
  GITHUB_TOKEN: "github-token",
  REVIEW_KEY: "review-key",
};

const payload = {
  action: "comment",
  issueNumber: 144,
  findingId: "runtime-domain-leak",
  decision: "optional-adapter",
  rationale: "This preserves compatibility while moving the domain contract.",
  summary: "Move health policy out of runtime.",
  question: "Which adapter seam?",
  revision: "54bbb62",
  evidence: [{ path: "engine/src/Sattle.Runtime/Adapters.cs", line: 664, detail: "AuthoringAdapter owns health rows." }],
  submissionId: "12345678-1234-4234-9234-123456789abc",
};

function request(body = payload, key = env.REVIEW_KEY) {
  return new Request("https://review.example/api/feedback", {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${key}` },
    body: JSON.stringify(body),
  });
}

describe("Cloudflare Pages functions", () => {
  it("reports repository feedback readiness without exposing secrets", async () => {
    const response = getConfig({ env });
    expect(await response.json()).toEqual({
      repository: "abossard/sattle",
      repositoryUrl: "https://github.com/abossard/sattle",
      feedbackEnabled: true,
    });
  });

  it.each([
    ["missing", "", { error: "Invalid review key." }, 401],
    ["wrong", "wrong-key", { error: "Invalid review key." }, 401],
  ])("rejects %s review keys", async (_name, key, expected, status) => {
    const response = await postFeedback({ request: request(payload, key), env });
    expect(response.status).toBe(status);
    expect(await response.json()).toEqual(expected);
  });

  it("posts an idempotent issue comment", async () => {
    const calls = [];
    vi.stubGlobal("fetch", vi.fn(async (url, init) => {
      calls.push([String(url), init]);
      if (String(url).includes("/comments?")) return new Response("[]", { status: 200 });
      return new Response(JSON.stringify({ html_url: "https://github.com/abossard/sattle/issues/144#issuecomment-1", id: 1 }), { status: 201 });
    }));
    const response = await postFeedback({ request: request(), env });
    expect(response.status).toBe(201);
    expect(await response.json()).toMatchObject({ action: "comment", duplicate: false });
    expect(calls).toHaveLength(2);
    expect(JSON.parse(calls[1][1].body).body).toContain("<!-- sattle-architecture-feedback:12345678-1234-4234-9234-123456789abc -->");
  });

  it("returns an existing comment for the same submission id", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url) => {
      if (String(url).includes("/comments?")) return new Response(JSON.stringify([
        { html_url: "https://github.com/abossard/sattle/issues/144#issuecomment-7", id: 7, body: "<!-- sattle-architecture-feedback:12345678-1234-4234-9234-123456789abc -->" },
      ]), { status: 200 });
      throw new Error("POST should not run");
    }));
    const response = await postFeedback({ request: request(), env });
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ id: 7, duplicate: true });
  });

  it.each([2, 3])("finds a duplicate beyond the first comment page (%i pages)", async (pages) => {
    const fetch = vi.fn(async (url) => {
      const page = Number(new URL(url).searchParams.get("page") || 1);
      const comments = page < pages ? Array.from({ length: 100 }, () => ({ body: "Older discussion" })) : [
        { html_url: "https://github.com/abossard/sattle/issues/144#issuecomment-7", id: 7, body: `<!-- sattle-architecture-feedback:${payload.submissionId} -->` },
      ];
      return Response.json(comments);
    });
    vi.stubGlobal("fetch", fetch);
    const response = await postFeedback({ request: request(), env });
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ id: 7, duplicate: true });
    expect(fetch).toHaveBeenCalledTimes(pages);
  });

  it("does not create a duplicate issue for the same submission id", async () => {
    const create = {
      ...payload,
      action: "create",
      issueNumber: undefined,
      title: "[Architecture] Make the runtime neutral",
    };
    vi.stubGlobal("fetch", vi.fn(async (url) => {
      if (String(url).startsWith("https://api.github.com/search/issues")) return new Response(JSON.stringify({
        items: [{ html_url: "https://github.com/abossard/sattle/issues/155", number: 155, body: "<!-- sattle-architecture-feedback:12345678-1234-4234-9234-123456789abc -->" }],
      }), { status: 200 });
      throw new Error("Create should not run for a duplicate");
    }));
    const response = await postFeedback({ request: request(create), env });
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ id: 155, action: "issue", duplicate: true });
  });

  it("validates issue batches", async () => {
    const invalid = new Request("https://review.example/api/issues?numbers=144,nope");
    const response = await getIssues({ request: invalid, env });
    expect(response.status).toBe(400);
  });
});
