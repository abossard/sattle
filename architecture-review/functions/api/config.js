import { json, repository } from "../_lib/github.js";

export function onRequestGet({ env }) {
  const repo = repository(env);
  return json({
    repository: `${repo.owner}/${repo.name}`,
    repositoryUrl: `https://github.com/${repo.owner}/${repo.name}`,
    feedbackEnabled: Boolean(env.GITHUB_TOKEN && env.REVIEW_KEY),
  });
}
