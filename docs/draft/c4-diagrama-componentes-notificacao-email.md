# Diagrama de Componentes (C4) — TodoApp WebAPI + Notificação por E-mail

Complementa [docs/c4-diagrama-componentes.md](../c4-diagrama-componentes.md) (diagrama base,
ainda válido e inalterado) mostrando o delta descrito no brief
[docs/draft/202607220120-todo-app-notificacao-email-status-brief.md](202607220120-todo-app-notificacao-email-status-brief.md)
e decidido em [ADR-0002](adr-0002-notificacao-email.md): um novo componente `EmailNotifier`,
acionado pelo slice `CompleteTask`, que dispara e-mail para um serviço SMTP externo à API.

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
        emailNotifier["EmailNotifier<br/><i>Componente novo (delta)</i><br/>Monta e dispara o e-mail de mudança de status"]
    end

    db[("Postgres<br/><i>Docker Compose</i><br/>Tabela de tarefas; schema criado na subida")]
    mail(["Serviço de E-mail (SMTP)<br/><i>Externo à API</i><br/>Fake SMTP em teste; provedor real configurável em produção"])

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

    completeTask -->|dispara notificação| emailNotifier
    emailNotifier -.->|usa| shared
    emailNotifier -->|envia e-mail| mail
```

## Leitura do diagrama
- Tudo que já existia no diagrama base permanece igual — o delta é só `EmailNotifier` e o
  `Serviço de E-mail (SMTP)` externo, mais a aresta nova saindo de `CompleteTask`.
- `EmailNotifier` vive dentro do container da API (é um componente do TodoApp WebAPI), mas o
  destino do e-mail (`mail`) é externo — por isso fica fora do `subgraph cli`, assim como o
  Postgres já ficava fora.
- Só `CompleteTask` aciona `EmailNotifier` nesta primeira versão (ver "Fora de escopo" no
  brief) — os demais slices (`AddTask`, `ListTasks`, `EditTask`, `RemoveTask`) não têm aresta
  para ele.
- `EmailNotifier` usa o `Shared Kernel` (ex.: dados da `Task` para montar o corpo do e-mail),
  mas não introduz uma camada de "Service" genérica — mantém o mesmo princípio de vertical
  slice do [ADR-0001](../adr-0001-vertical-slice.md), reforçado pelo [ADR-0002](adr-0002-notificacao-email.md)
  ao decidir que o disparo vive dentro do próprio slice `CompleteTask`.
