# ADR-0002: Adotar E-mail como Canal de Notificação de Mudança de Status

## Status
Proposto — rascunho, ainda não implementado. Acompanha o brief em
`docs/draft/202607220120-todo-app-notificacao-email-status-brief.md`, que também está em
`docs/draft/` até a sessão brownfield correspondente ser de fato iniciada.

## Contexto
O TodoApp WebAPI não tem modelo de usuário nem autenticação (fora de escopo desde o brief
original — ver `docs/202607211323-todo-app-brief.md`). Ao introduzir notificação de
mudança de status como delta, era preciso escolher um canal. As opções óbvias eram e-mail, SMS
e push notification — todas exigem, em algum grau, saber "para quem" notificar.

## Decisão
Adotar **e-mail** como canal de notificação, com destinatário e remetente configuráveis via
variável de ambiente (endereço único, fixo por implantação — não por usuário, já que não há
modelo de usuário). O disparo acontece dentro do slice do endpoint que muda o status
(`CompleteTask`), seguindo a vertical slice architecture do ADR-0001.

## Consequências

**Positivas:**
- Não exige criar um modelo de usuário/autenticação só para guardar um destino de notificação
  — um único endereço configurado já resolve o caso de uso atual.
- Testável localmente sem depender de provedor externo real: um fake SMTP/mock de client
  cobre o teste automatizado descrito no brief.
- Se encaixa no slice já existente (`CompleteTask`) sem precisar de uma camada nova
  compartilhada, mantendo a consequência positiva já registrada no ADR-0001.

**Negativas / trade-offs:**
- E-mail não é instantâneo (latência de entrega, filtro de spam) — aceitável porque o brief já
  define o envio como best-effort, síncrono, sem fila/retry nesta primeira versão.
- Um único destinatário fixo por implantação não escala para múltiplos usuários; se o app
  ganhar autenticação/multiusuário no futuro, esta decisão precisa ser revisitada.

## Alternativas consideradas
- **SMS** — rejeitado: exige provedor pago e número de telefone; sem modelo de usuário, não há
  onde guardar esse dado além de mais uma variável de ambiente, e SMS tem custo por envio que
  e-mail não tem.
- **Push notification** — rejeitado: exige um app cliente ou service worker registrado; o
  TodoApp é só uma WebAPI, sem frontend (fora de escopo desde o brief original).
- **Webhook genérico configurável** — considerado, mas não escolhido nesta primeira versão:
  adicionaria complexidade de configuração (validar/gerenciar URL de destino) sem ganho
  imediato sobre e-mail simples; pode ser revisitado se surgir a necessidade de integrar com
  outros sistemas.

## Referências
- Brief: `docs/draft/202607220120-todo-app-notificacao-email-status-brief.md`
- Decisão de arquitetura base: `docs/adr-0001-vertical-slice.md`
- Diagrama de componentes (delta): `docs/draft/c4-diagrama-componentes-notificacao-email.md`
