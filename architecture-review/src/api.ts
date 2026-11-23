import type { Discussion, LiveIssue, RuntimeConfig } from "./model";

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const body = await response.json().catch(() => ({ error: response.statusText }));
  if (!response.ok) throw new Error(typeof body.error === "string" ? body.error : `Request failed with ${response.status}`);
  return body as T;
}

export async function loadConfig(): Promise<RuntimeConfig> {
  try {
    return await json<RuntimeConfig>("/api/config");
  } catch {
    return { repository: "abossard/sattle", repositoryUrl: "https://github.com/abossard/sattle", feedbackEnabled: false };
  }
}

export async function loadIssues(numbers: number[]): Promise<Map<number, LiveIssue>> {
  if (numbers.length === 0) return new Map();
  try {
    const unique = [...new Set(numbers)];
    const issues: LiveIssue[] = [];
    for (let offset = 0; offset < unique.length; offset += 50) {
      const result = await json<{ issues: LiveIssue[] }>(`/api/issues?numbers=${unique.slice(offset, offset + 50).join(",")}`);
      issues.push(...result.issues);
    }
    return new Map(issues.map((issue) => [issue.number, issue]));
  } catch {
    return new Map();
  }
}

export function loadDiscussion(number: number): Promise<Discussion> {
  return json(`/api/discussion?number=${number}`);
}

const submissions = new Map<string, string>();

function newSubmissionId() {
  if (crypto.randomUUID) return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6]! & 15) | 64;
  bytes[8] = (bytes[8]! & 63) | 128;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export async function sendFeedback(reviewKey: string, payload: Record<string, unknown>): Promise<{ url: string; id: number; action: string }> {
  const fingerprint = JSON.stringify(payload);
  const submissionId = submissions.get(fingerprint) ?? newSubmissionId();
  submissions.set(fingerprint, submissionId);
  return json("/api/feedback", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${reviewKey}`,
    },
    body: JSON.stringify({ ...payload, submissionId }),
  });
}
