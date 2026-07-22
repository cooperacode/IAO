# Diagrama de Componentes (C4) — TodoApp WebAPI

Nível de **Componentes** do modelo C4, dentro do container "TodoApp WebAPI". Contexto maior:
1 cliente HTTP → TodoApp WebAPI (.NET / ASP.NET Core) → Postgres (Docker Compose). Este
diagrama detalha os componentes internos ao container da API, refletindo a decisão registrada
em [ADR-0001](adr-0001-vertical-slice.md): cada endpoint é um componente autocontido
(vertical slice), não uma camada compartilhada entre endpoints.

```mermaid
graph TD
    user(["Cliente HTTP<br/>Consome a API via requisições HTTP"])

    subgraph cli["TodoApp WebAPI (.NET / ASP.NET Core)"]
        entrypoint["API Entry Point<br/><i>Program.cs</i><br/>Recebe a requisição HTTP e roteia para o slice do endpoint"]
        addTask["AddTask<br/><i>Vertical Slice</i><br/>POST /tasks — parsing + regra + INSERT"]
        listTasks["ListTasks<br/><i>Vertical Slice</i><br/>GET /tasks — parsing + regra + SELECT"]
        completeTask["CompleteTask<br/><i>Vertical Slice</i><br/>PATCH /tasks/{id}/complete — parsing + regra + UPDATE"]
        editTask["EditTask<br/><i>Vertical Slice</i><br/>PUT /tasks/{id} — parsing + regra + UPDATE"]
        removeTask["RemoveTask<br/><i>Vertical Slice</i><br/>DELETE /tasks/{id} — parsing + regra + DELETE"]
        shared["Shared Kernel<br/><i>Biblioteca interna</i><br/>Task entity, conexão Postgres, exceções de domínio comuns"]
    end

    db[("Postgres<br/><i>Docker Compose</i><br/>Tabela de tarefas; schema criado na subida")]

    user -->|requisição HTTP| entrypoint
    entrypoint -->|roteia| addTask
    entrypoint -->|roteia| listTasks
    entrypoint -->|roteia| completeTask
    entrypoint -->|roteia| editTask
    entrypoint -->|roteia| removeTask

    addTask -->|INSERT| db
    listTasks -->|SELECT| db
    completeTask -->|UPDATE| db
    editTask -->|UPDATE| db
    removeTask -->|DELETE| db

    addTask -.->|usa| shared
    listTasks -.->|usa| shared
    completeTask -.->|usa| shared
    editTask -.->|usa| shared
    removeTask -.->|usa| shared
```

## Leitura do diagrama
- Cada slice (`AddTask`, `ListTasks`, `CompleteTask`, `EditTask`, `RemoveTask`) corresponde
  1:1 a um endpoint do backlog (`docs/202607211323-todo-app-brief.md`) e a um teste de integração próprio (via
  HTTP contra a API real).
- `Shared Kernel` é deliberadamente pequeno: só o que é genuinamente comum a todos os slices
  (entidade, conexão, exceções). Regra de negócio e acesso a dados específicos de cada
  endpoint não entram aqui.
- Não há Service ou Repository genérico compartilhado entre slices — ver
  [ADR-0001](adr-0001-vertical-slice.md) para a justificativa.
