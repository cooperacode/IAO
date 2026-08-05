# ADR-0002: Adopt Email as the Status-Change Notification Channel

## Status
Proposed — decision recorded, implementation not yet started. Accompanies the brief at
`202607220120-todo-app-notificacao-email-status-brief.md`, already promoted to the root of
`specs/` and ready to be read by the next `start` of the corresponding brownfield session.

## Context
The TodoApp WebAPI has no user model or authentication (out of scope since the original
brief — see `202607211323-todo-app-brief.md`). When introducing status-change
notification as a delta, a channel had to be chosen. The obvious options were email, SMS,
and push notification — all of which require, to some degree, knowing "who" to notify.

## Decision
Adopt **email** as the notification channel, with recipient and sender configurable via
environment variable (a single address, fixed per deployment — not per user, since there is
no user model). The trigger happens inside the slice of the endpoint that changes the
status (`CompleteTask`), following the vertical slice architecture from ADR-0001.

## Consequences

**Positive:**
- Does not require building a user/authentication model just to store a notification
  target — a single configured address already solves the current use case.
- Testable locally without depending on a real external provider: a fake SMTP/mock client
  covers the automated test described in the brief.
- Fits into the existing slice (`CompleteTask`) without needing a new shared layer,
  preserving the positive consequence already recorded in ADR-0001.

**Negative / trade-offs:**
- Email is not instantaneous (delivery latency, spam filtering) — acceptable because the
  brief already defines the send as best-effort, synchronous, with no queue/retry in this
  first version.
- A single fixed recipient per deployment does not scale to multiple users; if the app
  gains authentication/multi-user support in the future, this decision will need to be
  revisited.

## Alternatives considered
- **SMS** — rejected: requires a paid provider and a phone number; without a user model,
  there's nowhere to store that data other than yet another environment variable, and SMS
  has a per-send cost that email does not.
- **Push notification** — rejected: requires a registered client app or service worker; the
  TodoApp is only a WebAPI, with no frontend (out of scope since the original brief).
- **Configurable generic webhook** — considered but not chosen for this first version: it
  would add configuration complexity (validating/managing a destination URL) without an
  immediate gain over plain email; it can be revisited if the need arises to integrate with
  other systems.

## References
- Brief: `202607220120-todo-app-notificacao-email-status-brief.md`
- Base architecture decision: `adr-0001-vertical-slice.md`
- Component diagram (delta): `c4-diagrama-componentes-notificacao-email.md`
