# Component Diagram (C4) — TodoApp WebAPI + Email Notification

Complements [c4-diagrama-componentes.md](c4-diagrama-componentes.md) (base diagram, still
valid and unchanged), showing the delta described in the brief
[202607220120-todo-app-notificacao-email-status-brief.md](202607220120-todo-app-notificacao-email-status-brief.md)
and decided in [ADR-0002](adr-0002-notificacao-email.md): a new `EmailNotifier` component,
triggered by the `CompleteTask` slice, which sends an email to an SMTP service external to
the API.

```mermaid
graph TD
    user(["HTTP Client<br/>Consumes the API via HTTP requests"])

    subgraph cli["TodoApp WebAPI (.NET / ASP.NET Core)"]
        entrypoint["API Entry Point<br/><i>Program.cs</i><br/>Receives the HTTP request and routes it to the endpoint slice"]
        addTask["AddTask<br/><i>Vertical Slice</i><br/>POST /tasks — parsing + rule + INSERT"]
        listTasks["ListTasks<br/><i>Vertical Slice</i><br/>GET /tasks — parsing + rule + SELECT"]
        completeTask["CompleteTask<br/><i>Vertical Slice</i><br/>PATCH /tasks/{id}/complete — parsing + rule + UPDATE"]
        editTask["EditTask<br/><i>Vertical Slice</i><br/>PUT /tasks/{id} — parsing + rule + UPDATE"]
        removeTask["RemoveTask<br/><i>Vertical Slice</i><br/>DELETE /tasks/{id} — parsing + rule + DELETE"]
        shared["Shared Kernel<br/><i>Internal library</i><br/>Task entity, Postgres connection, common domain exceptions"]
        emailNotifier["EmailNotifier<br/><i>New component (delta)</i><br/>Builds and sends the status-change email"]
    end

    db[("Postgres<br/><i>Docker Compose</i><br/>Tasks table; schema created on startup")]
    mail(["Email Service (SMTP)<br/><i>External to the API</i><br/>Fake SMTP in tests; real provider configurable in production"])

    user -->|HTTP request| entrypoint
    entrypoint -->|routes| addTask
    entrypoint -->|routes| listTasks
    entrypoint -->|routes| completeTask
    entrypoint -->|routes| editTask
    entrypoint -->|routes| removeTask

    addTask -->|INSERT| db
    listTasks -->|SELECT| db
    completeTask -->|UPDATE| db
    editTask -->|UPDATE| db
    removeTask -->|DELETE| db

    addTask -.->|uses| shared
    listTasks -.->|uses| shared
    completeTask -.->|uses| shared
    editTask -.->|uses| shared
    removeTask -.->|uses| shared

    completeTask -->|triggers notification| emailNotifier
    emailNotifier -.->|uses| shared
    emailNotifier -->|sends email| mail
```

## Reading the diagram
- Everything that already existed in the base diagram stays the same — the delta is only
  `EmailNotifier` and the external `Email Service (SMTP)`, plus the new edge coming out of
  `CompleteTask`.
- `EmailNotifier` lives inside the API container (it is a component of the TodoApp WebAPI),
  but the email destination (`mail`) is external — that's why it sits outside the
  `subgraph cli`, just as Postgres already did.
- Only `CompleteTask` triggers `EmailNotifier` in this first version (see "Out of scope" in
  the brief) — the other slices (`AddTask`, `ListTasks`, `EditTask`, `RemoveTask`) have no
  edge to it.
- `EmailNotifier` uses the `Shared Kernel` (e.g., `Task` data to build the email body), but
  does not introduce a generic "Service" layer — it keeps the same vertical slice principle
  from [ADR-0001](adr-0001-vertical-slice.md), reinforced by
  [ADR-0002](adr-0002-notificacao-email.md) by deciding that the trigger lives within the
  `CompleteTask` slice itself.
