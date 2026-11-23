import { GitHubApiError, githubRequest, json, repository } from "../_lib/github.js";

export async function onRequestGet({ request, env }) {
  const number = Number(new URL(request.url).searchParams.get("number"));
  if (!Number.isSafeInteger(number) || number < 1) return json({ error: "number must be a positive issue number" }, { status: 400 });
  const repo = repository(env);
  const path = `/repos/${repo.owner}/${repo.name}/issues/${number}`;
  try {
    const issue = await githubRequest(env, path);
    const lastPage = Math.max(1, Math.ceil(issue.comments / 100));
    const pages = issue.comments ? [lastPage] : [];
    if (lastPage > 1 && issue.comments % 100 > 0 && issue.comments % 100 < 20) pages.unshift(lastPage - 1);
    const comments = (await Promise.all(pages.map((page) => githubRequest(env, `${path}/comments?per_page=100&page=${page}`)))).flat().slice(-20);
    return json({
      issue: { number: issue.number, title: issue.title, state: issue.state, body: issue.body || "", url: issue.html_url },
      totalComments: issue.comments,
      comments: comments.map((comment) => ({
        id: comment.id, body: comment.body || "", author: comment.user?.login || "unknown",
        url: comment.html_url, createdAt: comment.created_at,
      })),
    });
  } catch (error) {
    return json({ error: error.message }, { status: error instanceof GitHubApiError ? error.status : 502 });
  }
}
