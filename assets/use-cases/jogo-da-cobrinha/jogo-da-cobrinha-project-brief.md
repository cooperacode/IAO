# Caso de uso — Jogo da Cobrinha

## 1. Visão do projeto

Criar um jogo da cobrinha (Snake) executado diretamente no navegador, com
HTML5, CSS3 e JavaScript puro. O jogador controla uma cobra em uma arena,
coleta alimentos, aumenta a pontuação e tenta sobreviver pelo maior tempo
possível sem colidir com as bordas ou com o próprio corpo.

O projeto deve ser pequeno, autocontido e fácil de executar localmente,
servindo como exemplo de aplicação web interativa sem backend ou dependências
de frameworks.

## 2. Usuário e objetivo

- **Jogador:** inicia uma partida, controla a cobra e acompanha pontuação e
  recorde.
- **Objetivo:** coletar o maior número possível de alimentos e alcançar a maior
  pontuação antes de perder.

## 3. Escopo funcional

### Incluído

- Tela inicial com título, instruções e botão **Iniciar jogo**.
- Arena visual baseada em uma grade retangular.
- Cobra formada por segmentos, com movimentação contínua em intervalos
  regulares.
- Alimento posicionado em uma célula livre da arena.
- Crescimento da cobra ao coletar o alimento.
- Pontuação atualizada a cada alimento coletado.
- Detecção de colisão com as bordas e com o próprio corpo.
- Tela ou estado de **game over**, com pontuação final e opção de reiniciar.
- Persistência do maior recorde no `localStorage` do navegador.
- Controles por teclado usando as setas e as teclas `W`, `A`, `S` e `D`.
- Layout responsivo e instruções acessíveis em telas pequenas.

### Fora de escopo

- Backend, login, cadastro de jogadores ou ranking online.
- Multiplayer, inteligência artificial ou modo campanha.
- Frameworks ou bibliotecas JavaScript obrigatórias.
- Sistema de compras, anúncios ou telemetria.
- Compatibilidade com navegadores anteriores ao suporte básico a HTML5,
  `Canvas` e `localStorage`.

## 4. Requisitos funcionais

- **RF-001 — Iniciar partida:** ao selecionar **Iniciar jogo**, a arena deve
  aparecer com a cobra em uma posição inicial válida, uma direção inicial e um
  alimento em uma célula livre.
- **RF-002 — Controlar a cobra:** cada comando válido deve alterar a direção da
  cobra no próximo ciclo possível. A cobra não pode inverter diretamente para a
  direção oposta quando possui mais de um segmento.
- **RF-003 — Atualizar movimento:** a cobra deve avançar automaticamente em
  intervalos regulares, sem depender de novos comandos do jogador.
- **RF-004 — Coletar alimento:** quando a cabeça ocupar a célula do alimento, a
  cobra deve crescer, a pontuação deve aumentar e um novo alimento deve ser
  criado em posição livre.
- **RF-005 — Encerrar por colisão:** a partida deve terminar quando a cabeça
  atingir uma borda ou qualquer segmento do próprio corpo.
- **RF-006 — Exibir resultado:** ao perder, o jogo deve informar o fim da
  partida, mostrar a pontuação final e indicar se um novo recorde foi obtido.
- **RF-007 — Reiniciar:** o jogador deve conseguir iniciar uma nova partida sem
  recarregar a página, com estado, cobra, alimento e pontuação reinicializados.
- **RF-008 — Persistir recorde:** o maior valor alcançado deve ser salvo no
  `localStorage` e restaurado quando a página for aberta novamente.
- **RF-009 — Pausar e retomar:** a tecla `P` ou um controle visível deve pausar
  e retomar a movimentação sem perder o estado atual da partida.

## 5. Interface e experiência

- Usar um `canvas` HTML5 para desenhar a arena, a cobra e o alimento, ou uma
  solução equivalente baseada em elementos HTML; a escolha deve ser
  documentada.
- Separar visualmente título, placar, recorde, arena, instruções e ações.
- Diferenciar cobra, cabeça, alimento, grade e estados de pausa/game over por
  cor e/ou forma, sem depender somente de cor para comunicar o estado.
- Permitir uso confortável por teclado e oferecer botões de controle na tela
  para dispositivos sem teclado físico.
- Usar HTML semântico, foco visível, `aria-live` para mudanças de pontuação e
  mensagens de estado compreensíveis.
- Respeitar `prefers-reduced-motion` sempre que houver animações adicionais
  além do movimento principal do jogo.

## 6. Requisitos técnicos

- **HTML5:** estrutura semântica, área de jogo e controles acessíveis.
- **CSS3:** layout responsivo, estados visuais, contraste adequado e suporte a
  telas estreitas.
- **JavaScript:** estado do jogo, loop de atualização, entrada do usuário,
  colisões, pontuação, pausa e persistência.
- Não usar servidor para executar o jogo; abrir `index.html` deve ser
  suficiente para iniciar a aplicação.
- Manter o código separado em arquivos de estrutura, estilo e comportamento,
  por exemplo `index.html`, `styles.css` e `script.js`.
- Evitar variáveis globais desnecessárias e manter funções pequenas para
  criação do estado, atualização, renderização e tratamento de entrada.
- Usar uma única fonte de verdade para a grade, a cobra, a direção, o alimento,
  a pontuação e o estado da partida.

## 7. Critérios de aceitação

1. Ao abrir `index.html`, o usuário vê o nome do jogo, instruções, recorde e a
   ação para iniciar.
2. Uma partida iniciada movimenta a cobra automaticamente e exibe a arena sem
   erros no console do navegador.
3. As setas e `WASD` controlam a direção; uma inversão imediata de 180 graus é
   ignorada quando isso causaria colisão com o próprio corpo.
4. Ao coletar um alimento, a cobra aumenta de tamanho, a pontuação é
   incrementada e o novo alimento não aparece sobre a cobra.
5. Ao colidir com a borda ou consigo mesma, o movimento para e o jogador vê a
   pontuação final e uma ação clara para jogar novamente.
6. Pausar e retomar preserva exatamente a posição, a direção, o alimento e a
   pontuação da partida.
7. O recorde permanece disponível após atualizar ou fechar e reabrir a página.
8. A interface continua utilizável em uma viewport estreita, sem cortes ou
   rolagem horizontal desnecessária.
9. O fluxo principal pode ser exercitado manualmente e as regras puras de
   movimento, colisão, crescimento e pontuação possuem testes automatizados ou
   uma estratégia de verificação equivalente documentada.

## 8. Regras de jogo delegadas

- A dimensão inicial da grade, velocidade, cores e valor de cada alimento são
  escolhas de implementação, desde que documentadas e consistentes.
- A pontuação deve ser monotonicamente crescente durante uma partida; o valor
  por alimento pode ser fixo.
- O alimento deve ser sorteado apenas entre células livres. Se não houver
  células livres, a partida deve ser considerada vencida ou encerrada com uma
  mensagem explícita.
- O estado inicial deve ser determinístico o suficiente para permitir testes,
  com o gerador aleatório podendo ser injetado ou substituído em testes.

## 9. Fatias sugeridas

1. **Estrutura e tela inicial:** HTML semântico, estilos base, placar e estados
   de início/game over.
2. **Motor da arena:** modelo de grade, cobra, alimento, loop de atualização e
   renderização.
3. **Controles e regras:** teclado, controles touch, mudança de direção,
   crescimento e colisões.
4. **Estados persistentes:** pausa, reinício, recorde no `localStorage` e
   mensagens acessíveis.
5. **Qualidade visual e verificação:** responsividade, acessibilidade, testes
   das regras e validação do fluxo completo no navegador.

## 10. Resultado esperado

Um projeto web estático que possa ser executado localmente abrindo
`index.html`, apresente uma experiência completa e jogável de Snake e tenha
estrutura suficientemente clara para servir de base a futuras extensões,
como níveis de dificuldade, temas visuais ou novos tipos de alimento.
