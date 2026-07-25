# ADR-0001: Adotar Vertical Slice Architecture para o TodoApp WebAPI

## Status
Aceito

## Contexto
O TodoApp WebAPI tem múltiplos endpoints independentes (adicionar, listar, concluir, editar,
remover, filtrar) e está migrando a persistência de arquivo JSON para Postgres, com cada
funcionalidade coberta por um teste de integração real contra o banco (sem mocks). O
backlog já é gerado como **features pequenas, verticais e verificáveis** — cada uma
implementável e testável isoladamente, sem depender do estado das demais. Uma organização
de código em camadas técnicas horizontais (Controller → Service → Repository) compartilhadas
entre todos os endpoints tende a contrariar essa premissa: uma mudança num endpoint arrisca
regressão nos demais, e testar uma única funcionalidade exige montar a camada inteira.

## Decisão
Organizar o código por endpoint (**vertical slice**), não por camada técnica horizontal. Cada
slice contém, num único lugar coeso: parsing da requisição HTTP, regra de negócio e acesso a
dados (query/command contra Postgres) daquela única operação. Não há Controller, Service ou
Repository genéricos compartilhados entre slices — compartilha-se apenas o que é
genuinamente comum a todas (entidade `Task`, conexão/pool com o Postgres, exceções de
domínio), como um *shared kernel* mínimo.

Mapeamento direto: cada feature do backlog (ver `docs/202607211323-todo-app-brief.md`) = 1 slice = 1
pasta/namespace = 1 teste de integração próprio.

## Consequências

**Positivas:**
- Cada feature é implementada, testada e revisada isoladamente — alinhado ao requisito de
  features "pequenas, verticais e verificáveis" que já guia a geração do backlog.
- Adicionar uma funcionalidade nova não exige tocar em camadas compartilhadas usadas por
  outros endpoints, reduzindo o risco de regressão cruzada.
- Testes de integração ficam mais fáceis de localizar e rodar: 1 teste ≈ 1 slice, sem setup
  de camadas que a feature sob teste nem usa.
- A regra de negócio de cada slice fica isolada do parsing HTTP e do acesso a dados dentro do
  próprio slice, o que permite testá-la em um teste de unidade rápido (sem HTTP, sem
  Postgres), complementar ao teste de integração do endpoint completo.

**Negativas / trade-offs:**
- Duplicação intencional entre slices (cada um implementa seu próprio acesso a dados) em vez
  de reuso via uma camada de serviço comum — aceito como custo do desacoplamento.
- Exige disciplina para não deixar o *shared kernel* crescer de volta para uma camada
  compartilhada grande com o tempo.

## Alternativas consideradas
- **Arquitetura em camadas (Controller → Service → Repository)** — rejeitada: acopla os
  endpoints a uma Service layer e uma Repository genéricas compartilhadas, tornando mudanças
  em um endpoint arriscadas para os demais.
- **Clean/Onion Architecture** — rejeitada: o número de casos de uso é pequeno e estável para
  o escopo atual (WebAPI, poucos endpoints); a indireção adicional (interfaces por camada,
  inversão de dependência) não se paga quando as fatias já são pequenas e verificáveis por si
  só.

## Referências
- Diagrama de componentes: `docs/c4-diagrama-componentes.md`
- Backlog/funcionalidades: `docs/202607211323-todo-app-brief.md`
