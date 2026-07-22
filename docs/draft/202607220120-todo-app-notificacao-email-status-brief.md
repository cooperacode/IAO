# Notificação por E-mail ao Mudar Status de Tarefa — TodoApp WebAPI

**Tipo:** Brownfield
**Run-id (provisório):** `202607220120`

## Status
**Rascunho, não ativo.** Guardado em `docs/draft/` de propósito — não é lido pelo `start`
(glob não-recursivo em `docs/`). O app-alvo (`todoapp-webapi`) já existe, então este brief
descreve um **delta**, não o app inteiro — o `dev-initializer` deve cair no caminho brownfield
do Step 0 (inspeção limitada a metadado, reaproveitar `$VERIFY_CMD` e convenções já
estabelecidas, quebrar só o delta em features).

Para disparar uma sessão real do harness com este brief, mova o arquivo para a raiz de `docs/`
antes de rodar `start`. O run-id acima é provisório (gerado no momento em que este rascunho foi
escrito); a branch de trabalho real ganha seu próprio run-id no Step 1 do `dev-initializer`
(`git checkout -b <YYYYMMDDHHMM>-<nome-descritivo>`), que não precisa coincidir com este.

## Contexto
Delta sobre o TodoApp WebAPI já implementado — ver a decisão de arquitetura em
[docs/adr-0001-vertical-slice.md](../adr-0001-vertical-slice.md), a visão de componentes em
[docs/c4-diagrama-componentes.md](../c4-diagrama-componentes.md) e o brief original do app em
[docs/202607211323-todo-app-brief.md](../202607211323-todo-app-brief.md). Hoje as
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
