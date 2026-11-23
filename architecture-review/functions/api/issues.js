import { GitHubApiError, githubRequest, json, repository } from "../_lib/github.js";

function parseNumbers(request) {
  const raw = new URL(request.url).searchParams.get("numbers") || "";
  const numbers = [...new Set(raw.split(",").map((value) => value.trim()).filter(Boolean).map(Number))];
  if (numbers.length === 0 || numbers.length > 50 || numbers.some((number) => !Number.isInteger(number) || number < 1)) {
    throw new RangeError("numbers must contain 1 to 50 positive issue numbers");
  }
  return numbers;
}

export async function onRequestGet({ request, env }) {
  let numbers;
  try {
    numbers = parseNumbers(request);
  } catch (error) {
    return json({ error: error.message }, { status: 400 });
  }
  const repo = repository(env);
  try {
    const issues = await Promise.all(numbers.map(async (number) => {
      const issue = await githubRequest(env, `/repos/${repo.owner}/${repo.name}/issues/${number}`);
      return {
        number: issue.number,
        title: issue.title,
        state: issue.state,
        url: issue.html_url,
        labels: (issue.labels || []).map((label) => typeof label === "string" ? label : label.name),
        updatedAt: issue.updated_at,
      };
    }));
    return json({ issues });
  } catch (error) {
    const status = error instanceof GitHubApiError ? error.status : 502;
    return json({ error: error.message }, { status });
  }
}
