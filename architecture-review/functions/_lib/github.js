const API = "https://api.github.com";

export class GitHubApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

export function repository(env) {
  return {
    owner: env.GITHUB_OWNER || "abossard",
    name: env.GITHUB_REPOSITORY || "sattle",
  };
}

export function githubHeaders(env, extra = {}) {
  const headers = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "sattle-architecture-review",
    ...extra,
  };
  if (env.GITHUB_TOKEN) headers.Authorization = `Bearer ${env.GITHUB_TOKEN}`;
  return headers;
}

export async function githubRequest(env, path, init = {}) {
  const response = await fetch(`${API}${path}`, {
    ...init,
    headers: githubHeaders(env, init.headers),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    const message = typeof body.message === "string" ? body.message : `GitHub returned ${response.status}`;
    throw new GitHubApiError(response.status, message);
  }
  return body;
}

export function json(value, init = {}) {
  return new Response(JSON.stringify(value), {
    ...init,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
      ...(init.headers || {}),
    },
  });
}
