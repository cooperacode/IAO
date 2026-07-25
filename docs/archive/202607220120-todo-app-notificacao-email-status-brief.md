# Notificação por E-mail ao Mudar Status de Tarefa — TodoApp WebAPI

**Tipo:** Brownfield

## Contexto
Delta sobre o TodoApp WebAPI já implementado — ver a decisão de arquitetura em
[adr-0001-vertical-slice.md](adr-0001-vertical-slice.md), a visão de componentes em
[c4-diagrama-componentes.md](c4-diagrama-componentes.md) e o brief original do app em
[202607211323-todo-app-brief.md](202607211323-todo-app-brief.md). Hoje as
mudanças de status de uma tarefa (concluir, editar, remover, filtrar) acontecem em silêncio —
não existe nenhuma notificação externa quando o status muda. A escolha do e-mail como canal de
notificação (em vez de SMS ou push) está registrada em
[ADR-0002](adr-0002-notificacao-email.md), e o componente novo que ela introduz está desenhado
em [c4-diagrama-componentes-notificacao-email.md](c4-diagrama-componentes-notificacao-email.md).

## Objetivo
Disparar o envio de um e-mail sempre que uma tarefa mudar de status.

## Funcionalidades desejadas (delta — não o app inteiro)
1. **Disparo ao concluir tarefa** — `PATCH /tasks/{id}/complete` passa a disparar um e-mail
   após persistir a mudança de status no Postgres.
2. **Conteúdo do e-mail** — id da tarefa, título, status anterior e novo status.
3. **Destinatário/remetente configuráveis** — via variável de ambiente, sem hardcode.
4. **Teste cobrindo o disparo** — real ou contra um fake SMTP / mock do serviço de e-mail (a
   decidir na implementação); o requisito de "sem mocks" do ADR-0001 vale para o fluxo
   HTTP+Postgres já existente, não bloqueia um double de teste para o serviço de e-mail em si.

## Regras / restrições
- Respeitar a vertical slice architecture já estabelecida (ADR-0001): o disparo de e-mail vive
  dentro do slice do endpoint que já muda o status (`CompleteTask`), ou é extraído como uma
  responsabilidade pequena e explicitamente justificada — não recriar uma camada de "Service"
  genérica compartilhada.
- Sem dependência de provedor de e-mail real de produção — usar algo testável localmente (ex.:
  fake SMTP, mock de client).

## Fora de escopo
- Templates de e-mail elaborados, múltiplos idiomas.
- Outros canais de notificação (SMS, push).
- Fila/retry robusto de envio — pode ser síncrono e best-effort nesta primeira versão.

## Critério de "pronto"
Concluir uma tarefa via `PATCH /tasks/{id}/complete` dispara o e-mail com o conteúdo descrito
acima, coberto por teste automatizado, sem regressão nos endpoints/testes já existentes do
TodoApp WebAPI.

## Cenários de teste (Gherkin)

### Funcionalidade: Disparo e conteúdo do e-mail ao concluir tarefa

```gherkin
Funcionalidade: Notificar por e-mail ao concluir tarefa
  Como responsável por acompanhar as tarefas
  Quero receber um e-mail quando uma tarefa for concluída
  Para saber da mudança sem precisar consultar a API

  Cenário: Caminho feliz - concluir tarefa dispara e-mail com o conteúdo esperado
    Dado que existe uma tarefa pendente com id 1 e título "Comprar leite"
    E o serviço de e-mail (fake SMTP) está disponível
    Quando eu envio PATCH /tasks/1/complete
    Então a resposta tem status 200 OK
    E um e-mail é enviado contendo id 1, título "Comprar leite", status anterior "pendente"
      e novo status "concluída"

  Cenário: Fluxo de exceção - falha no envio do e-mail não compromete a mudança de status
    Dado que existe uma tarefa pendente com id 1 e título "Comprar leite"
    E o serviço de e-mail (fake SMTP) está indisponível
    Quando eu envio PATCH /tasks/1/complete
    Então a resposta tem status 200 OK
    E o status da tarefa 1 no Postgres passa a ser "concluída"
    E a falha no envio do e-mail é registrada (log), sem quebrar a requisição

  Cenário: Fluxo alternativo - concluir tarefa já concluída não reenvia o e-mail
    Dado que existe uma tarefa com id 1 e status "concluída"
    Quando eu envio PATCH /tasks/1/complete novamente
    Então a resposta tem status 200 OK
    E nenhum e-mail novo é disparado, pois não houve transição de status
```

### Funcionalidade: Destinatário e remetente configuráveis

```gherkin
Funcionalidade: Configurar destinatário e remetente do e-mail de notificação
  Como responsável por operar o TodoApp WebAPI
  Quero definir o destinatário e o remetente do e-mail via configuração
  Para não depender de valores fixos no código

  Cenário: Caminho feliz - envio usa destinatário e remetente configurados
    Dado que as variáveis de ambiente de destinatário e remetente estão definidas
    E existe uma tarefa pendente com id 1
    Quando eu envio PATCH /tasks/1/complete
    Então o e-mail é enviado com o remetente e o destinatário definidos nas variáveis de ambiente

  Cenário: Fluxo de exceção - configuração de destinatário/remetente ausente
    Dado que a variável de ambiente de destinatário (ou remetente) não está definida
    E existe uma tarefa pendente com id 1
    Quando eu envio PATCH /tasks/1/complete
    Então a resposta tem status 200 OK
    E a mudança de status é persistida no Postgres
    E o disparo do e-mail falha de forma explícita (log claro), sem usar um valor hardcoded
```
