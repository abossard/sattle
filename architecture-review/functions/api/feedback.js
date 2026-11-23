import { GitHubApiError, githubRequest, json, repository } from "../_lib/github.js";

const MAX_BODY_BYTES = 24_000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function secureEqual(a, b) {
  const aa = new TextEncoder().encode(a);
  const bb = new TextEncoder().encode(b);
  let different = aa.length ^ bb.length;
  for (let index = 0; index < Math.max(aa.length, bb.length); index++) different |= (aa[index] || 0) ^ (bb[index] || 0);
  return different === 0;
}

function requiredString(value, name, max) {
  if (typeof value !== "string" || value.trim() === "" || value.length > max) throw new TypeError(`${name} must be 1 to ${max} characters`);
  return value.trim();
}

function normalize(payload) {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new TypeError("body must be a JSON object");
  const action = payload.action;
  if (action !== "comment" && action !== "create") throw new TypeError("action must be comment or create");
  const submissionId = requiredString(payload.submissionId, "submissionId", 64);
  if (!UUID.test(submissionId)) throw new TypeError("submissionId must be a UUID");
  const issueNumber = action === "comment" ? Number(payload.issueNumber) : undefined;
  if (action === "comment" && (!Number.isInteger(issueNumber) || issueNumber < 1)) throw new TypeError("issueNumber must be a positive integer");
  const findingId = requiredString(payload.findingId, "findingId", 120);
  const decision = requiredString(payload.decision, "decision", 160);
  const rationale = requiredString(payload.rationale, "rationale", 8000);
  const summary = requiredString(payload.summary, "summary", 2000);
  const question = requiredString(payload.question, "question", 2000);
  const revision = requiredString(payload.revision, "revision", 80);
  const title = action === "create" ? requiredString(payload.title, "title", 180) : null;
  const evidence = Array.isArray(payload.evidence) ? payload.evidence.slice(0, 20).map((item, index) => {
    if (!item || typeof item !== "object" || Array.isArray(item)) throw new TypeError(`evidence[${index}] must be an object`);
    return {
      path: requiredString(item.path, `evidence[${index}].path`, 300),
      line: Number.isInteger(item.line) && item.line > 0 ? item.line : 1,
      detail: requiredString(item.detail, `evidence[${index}].detail`, 1200),
    };
  }) : [];
  return { action, submissionId, issueNumber, findingId, decision, rationale, summary, question, revision, title, evidence };
}

function marker(input) {
  return `<!-- sattle-architecture-feedback:${input.submissionId} -->`;
}

function markdown(input) {
  return [
    marker(input),
    "## Architecture dashboard response",
    "",
    `- Finding: \`${input.findingId}\``,
    `- Choice: \`${input.decision}\``,
    `- Dashboard revision: \`${input.revision}\``,
    "",
    "### Rationale",
    input.rationale,
    "",
    "### Review question",
    input.summary,
    "",
    input.question,
    "",
    ...(input.evidence.length ? [
      "### Evidence",
      ...input.evidence.map((item) => `- \`${item.path}:${item.line}\` ${item.detail}`),
      "",
    ] : []),
    "> Submitted by the Sattle architecture review dashboard. GitHub remains the durable decision source.",
  ].join("\n");
}

async function existingComment(env, repo, issueNumber, id) {
  const markerText = marker({ submissionId: id });
  for (let page = 1; page <= 20; page++) {
    const comments = await githubRequest(env, `/repos/${repo.owner}/${repo.name}/issues/${issueNumber}/comments?per_page=100&page=${page}`);
    const existing = comments.find((comment) => typeof comment.body === "string" && comment.body.includes(markerText));
    if (existing || comments.length < 100) return existing;
  }
  throw new GitHubApiError(409, "This issue has too many comments to safely retry. Check GitHub before submitting again.");
}

async function existingIssue(env, repo, id) {
  const query = `repo:${repo.owner}/${repo.name} in:body "sattle-architecture-feedback:${id}"`;
  const result = await githubRequest(env, `/search/issues?q=${encodeURIComponent(query)}&per_page=5`);
  return result.items?.find((issue) => typeof issue.body === "string" && issue.body.includes(marker({ submissionId: id })));
}

export async function onRequestPost({ request, env }) {
  if (!env.REVIEW_KEY || !env.GITHUB_TOKEN) return json({ error: "Feedback is not configured. Set REVIEW_KEY and GITHUB_TOKEN on the Pages project." }, { status: 503 });
  const auth = request.headers.get("Authorization") || "";
  const key = auth.startsWith("Bearer ") ? auth.slice(7) : "";
  if (!key || !secureEqual(key, env.REVIEW_KEY)) return json({ error: "Invalid review key." }, { status: 401 });

  const declared = Number(request.headers.get("Content-Length") || 0);
  if (declared > MAX_BODY_BYTES) return json({ error: "Request body is too large." }, { status: 413 });
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > MAX_BODY_BYTES) return json({ error: "Request body is too large." }, { status: 413 });

  let input;
  try {
    input = normalize(JSON.parse(text));
  } catch (error) {
    return json({ error: error.message }, { status: 400 });
  }

  const repo = repository(env);
  const body = markdown(input);
  try {
    if (input.action === "comment") {
      const existing = await existingComment(env, repo, input.issueNumber, input.submissionId);
      if (existing) return json({ url: existing.html_url, id: existing.id, action: "comment", duplicate: true });
      const comment = await githubRequest(env, `/repos/${repo.owner}/${repo.name}/issues/${input.issueNumber}/comments`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ body }),
      });
      return json({ url: comment.html_url, id: comment.id, action: "comment", duplicate: false }, { status: 201 });
    }
    const existing = await existingIssue(env, repo, input.submissionId);
    if (existing) return json({ url: existing.html_url, id: existing.number, action: "issue", duplicate: true });
    const issue = await githubRequest(env, `/repos/${repo.owner}/${repo.name}/issues`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: input.title, body }),
    });
    return json({ url: issue.html_url, id: issue.number, action: "issue", duplicate: false }, { status: 201 });
  } catch (error) {
    const status = error instanceof GitHubApiError ? error.status : 502;
    return json({ error: error.message }, { status });
  }
}
