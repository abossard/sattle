# Sattle architecture review

Generated, evidence-linked architecture feedback board for Sattle. The static review explains current code and open questions. GitHub issues and issue comments are the only durable decision record.

## Local

```text
npm ci
npm run generate
npm run dev
```

Run all checks with:

```text
npm run check
```

With the Pages server running, `npm run test:browser -- http://<host>:<port>` runs real Chrome checks for navigation, drafts, feedback retries, responsive layout and large-queue DOM limits. Set `CHROME` to the browser executable outside macOS. Set `AXE_PATH` to an installed `axe-core/axe.min.js` for accessibility checks and `SCREENSHOT` to save a PNG. Browser feedback writes are intercepted; the test does not post review comments to GitHub.

The review generator validates unique code anchors against committed HEAD, resolves exact source lines, computes file churn, scores findings, and writes deterministic `public/review.json`. That generated file is ignored and recreated by test/build commands, so commits do not contain a stale self-referential revision. Uncommitted engine changes are deliberately excluded from the public review. The date shown is the evidence commit date, not the current time.

Findings are curated architecture hypotheses, not automated proof of a defect. The generator refreshes provenance and priorities, not their meaning. File churn does not prove a decision reversal. Recorded direction changes link to their owning GitHub issues.

To extend the canvas, edit `data/review-source.json`: add an issue-owned finding, evidence with unique `anchor` text, decision options and settlement checks. Add modules and edges for new boundaries. `npm run generate -- --check` detects stale output without writing it. `--source <path>` and `--output <path>` support alternate review sets.

## LAN HTTP

Build first, then use an available port:

```text
npm run build
npm run pages:dev -- --ip 0.0.0.0 --port 59314 --local-protocol http
```

Local feedback requires `GITHUB_TOKEN` and `REVIEW_KEY` in a gitignored `.dev.vars` file. Use a fine-grained GitHub token with Issues read/write for this repository. Protect the file with mode `0600`. Nothing is sent to GitHub until a reviewer submits.

Enter the review key in the form, or open a privately shared `/#review-key=<key>` link. The fragment is removed immediately and the key stays in tab memory. Plain HTTP does not encrypt that key in feedback requests, so use LAN HTTP only on a trusted network. Public deployments should use Pages HTTPS.

The selected issue's brief and latest 20 comments refresh every 30 seconds while the tab is visible and immediately after submission. Live GitHub read failures are shown explicitly. Drafts survive view/filter changes in tab memory only. Neither drafts nor issue comments are automatically accepted decisions.

For assistant notification, monitor repository issue comments for `<!-- sattle-architecture-feedback:UUID -->`. A session-level two-minute monitor is active for this review session; it is not a permanent service. Future sessions should read #154 and the latest handover on #122 and re-arm monitoring when requested.

## Cloudflare Pages

Use `architecture-review` as the Pages root directory, run `npm ci && npm run check`, and publish `dist`. Pages Functions are under `functions/`.

`.github/workflows/architecture-review-pages.yml` performs the same check on `main` and deploys when `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` are configured as repository secrets. Without them, checks still run and the workflow explicitly reports that deployment was skipped.

Set these Pages variables:

```text
GITHUB_OWNER=abossard
GITHUB_REPOSITORY=sattle
```

Set these as encrypted secrets:

```text
GITHUB_TOKEN=<fine-grained token with Issues: read and write>
REVIEW_KEY=<long random review key>
```

Never expose the GitHub token to the browser. Anyone with the review key can post an issue comment or create an architecture issue, so rotate it independently from the GitHub token.

Manual local Pages run:

```text
npm run build
npx wrangler pages dev dist
```

Manual deploy:

```text
npm run build
npx wrangler pages deploy dist --project-name=sattle-architecture-review
```

## Durable decision loop

1. The generated board ranks uncertainty, conflict, churn, gaps, leaks, and scale risk.
2. A reviewer chooses an option and adds a rationale.
3. The Pages Function posts an idempotent comment to the linked issue, or creates a new architecture issue.
4. The next review generation reads current code and file history. The live dashboard reads current issue titles and state.
5. The browser stores no accepted decisions. Unsaved drafts and the review key stay in memory only.

Retries of the same answer reuse a submission ID within the tab. The Function checks up to 2,000 existing comments before posting, and refuses rather than risking a duplicate beyond that limit. This protects sequential retries, not simultaneous requests or issue-creation retries before GitHub search indexes a new issue. GitHub is the record, so check the live discussion before resubmitting after reloading the tab.
