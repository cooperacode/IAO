# Email Notification on Task Status Change — TodoApp WebAPI

**Type:** Brownfield

## Context
Delta on top of the already-implemented TodoApp WebAPI — see the architecture decision in
[adr-0001-vertical-slice.md](adr-0001-vertical-slice.md), the component view in
[c4-diagrama-componentes.md](c4-diagrama-componentes.md), and the original app brief in
[202607211323-todo-app-brief.md](202607211323-todo-app-brief.md). Today, a task's
status changes (complete, edit, remove, filter) happen silently — there is no external
notification when the status changes. The choice of email as the notification channel
(instead of SMS or push) is recorded in
[ADR-0002](adr-0002-notificacao-email.md), and the new component it introduces is designed
in [c4-diagrama-componentes-notificacao-email.md](c4-diagrama-componentes-notificacao-email.md).

## Goal
Trigger the sending of an email whenever a task changes status.

## Desired functionality (delta — not the whole app)
1. **Trigger on task completion** — `PATCH /tasks/{id}/complete` now triggers an email
   after persisting the status change to Postgres.
2. **Email content** — task id, title, previous status, and new status.
3. **Configurable recipient/sender** — via environment variable, no hardcoding.
4. **Test covering the trigger** — real or against a fake SMTP / mock of the email service (to
   be decided during implementation); the "no mocks" requirement from ADR-0001 applies to the
   existing HTTP+Postgres flow, and does not block a test double for the email service itself.

## Rules / constraints
- Respect the already-established vertical slice architecture (ADR-0001): the email trigger
  lives inside the slice of the endpoint that already changes the status (`CompleteTask`), or
  is extracted as a small, explicitly justified responsibility — do not recreate a generic
  shared "Service" layer.
- No dependency on a real production email provider — use something locally testable (e.g.,
  fake SMTP, mock client).

## Out of scope
- Elaborate email templates, multiple languages.
- Other notification channels (SMS, push).
- Robust send queue/retry — can be synchronous and best-effort in this first version.

## Definition of done
Completing a task via `PATCH /tasks/{id}/complete` triggers the email with the content
described above, covered by an automated test, with no regression in the existing
endpoints/tests of the TodoApp WebAPI.

## Test scenarios (Gherkin)

### Feature: Email trigger and content on task completion

```gherkin
Feature: Notify by email when a task is completed
  As someone responsible for tracking tasks
  I want to receive an email when a task is completed
  So that I know about the change without having to query the API

  Scenario: Happy path - completing a task triggers an email with the expected content
    Given a pending task exists with id 1 and title "Buy milk"
    And the email service (fake SMTP) is available
    When I send PATCH /tasks/1/complete
    Then the response has status 200 OK
    And an email is sent containing id 1, title "Buy milk", previous status "pending"
      and new status "completed"

  Scenario: Exception flow - email send failure does not compromise the status change
    Given a pending task exists with id 1 and title "Buy milk"
    And the email service (fake SMTP) is unavailable
    When I send PATCH /tasks/1/complete
    Then the response has status 200 OK
    And the status of task 1 in Postgres becomes "completed"
    And the email send failure is logged, without breaking the request

  Scenario: Alternative flow - completing an already-completed task does not resend the email
    Given a task exists with id 1 and status "completed"
    When I send PATCH /tasks/1/complete again
    Then the response has status 200 OK
    And no new email is triggered, since there was no status transition
```

### Feature: Configurable recipient and sender

```gherkin
Feature: Configure the recipient and sender of the notification email
  As someone responsible for operating the TodoApp WebAPI
  I want to set the email recipient and sender via configuration
  So that I don't depend on hardcoded values in the code

  Scenario: Happy path - sending uses the configured recipient and sender
    Given the recipient and sender environment variables are set
    And a pending task exists with id 1
    When I send PATCH /tasks/1/complete
    Then the email is sent with the sender and recipient set in the environment variables

  Scenario: Exception flow - missing recipient/sender configuration
    Given the recipient (or sender) environment variable is not set
    And a pending task exists with id 1
    When I send PATCH /tasks/1/complete
    Then the response has status 200 OK
    And the status change is persisted to Postgres
    And the email trigger fails explicitly (clear log), without using a hardcoded value
```
