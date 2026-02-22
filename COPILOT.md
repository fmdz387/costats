# Copilot reference notes

This file captures Copilot-related implementation notes and external references used during development.

## Existing costats Copilot approach
- Current implementation is based on https://github.com/Finesssee/Win-CodexBar/
- Uses GitHub Copilot internal usage API.
- Endpoint: `GET https://api.github.com/copilot_internal/user`
- Required headers (current):
  - `Authorization: token <github_oauth_token>`
  - `Accept: application/json`
  - `User-Agent: GitHubCopilotChat/0.26.7`
  - `Editor-Version: vscode/1.96.2`
  - `Editor-Plugin-Version: copilot-chat/0.26.7`
  - `X-GitHub-Api-Version: 2025-04-01`
- Key fields used:
  - `copilotPlan` → plan label
  - `quotaSnapshots.premiumInteractions.percentRemaining` → premium usage percent (used = 100 - remaining)
  - `quotaSnapshots.chat.percentRemaining` → chat usage percent (used = 100 - remaining)
  - `quotaResetDate` → reset timestamp (if present)

## Reference: Copilot Premium Usage Monitor
Repo: https://github.com/Fail-Safe/CopilotPremiumUsageMonitor
Note: This is a potential future enhancement or alternative approach, not the current implementation.

What it uses:
- Personal spend: `GET /users/{username}/settings/billing/usage`
  - Filters `usageItems` to `product == "Copilot"`.
  - Computes total requests, included requests (derived from discount/price), overage, and spend.
  - Requires PAT with **Plan: read-only** (Enhanced Billing).
- Org metrics: `GET /orgs/{org}/copilot/metrics`
  - Summarizes engaged users and code suggestions over the last 28 days.

What it displays:
- Budget meter (percent used).
- Included vs overage and spend (personal mode).
- Org metrics (engaged users, total code suggestions).
- Optional trend/history in its panel.

## Design implications for costats
- The internal usage API is best for quota bars (premium + chat).
- Billing endpoints can complement with spend/included/overage if we want to add a cost view later.
