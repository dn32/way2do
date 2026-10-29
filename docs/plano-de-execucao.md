# Plano de execução — Way2Do

Como cada etapa será executada, quem faz o quê (pessoa e IA), como cada etapa é verificada e quais são os passos
até a entrega. O **o quê** e o **porquê** estão no [escopo](escopo.md); os diagramas, na [arquitetura](arquitetura.md).

---

## 1. Modelo de trabalho

### Papéis

| Quem | Papel |
|---|---|
| **Marcelo** | **conduz o projeto como responsável técnico**: define os padrões de trabalho (portões de DESENVOLVA e COMMIT, revisão obrigatória antes de cada commit, histórico de commits espaçado, credenciais fora do repositório); levanta requisitos e propõe soluções de arquitetura; **questiona** as propostas da IA e pede avaliação contra o enunciado antes de aceitar; decide produto, escopo e arquitetura; encomenda e confere revisões, apontando erros que a IA deixou passar; revisa, pede ajustes e aprova as telas geradas no Claude Design; opera o GitHub, a VPS, a Cloudflare e o e-mail de entrega |
| **Claude Code** (sessão principal) | analisa o enunciado; propõe alternativas com prós, contras e recomendação; avalia as propostas do Marcelo (inclusive apontando riscos); redige e mantém os documentos; implementa, escreve os testes, roda, revisa e corrige |
| **Claude Design** | ferramenta em que a IA gera as telas a partir do briefing (apêndice A), para revisão e aprovação do Marcelo |
| **Subagentes do Claude Code** | trabalho paralelo e independente (ex.: consumidor simulado em *worktree* separado) e revisões com olhar não ancorado na implementação |

### Regras de funcionamento

- **Portões explícitos**: código só começa com **DESENVOLVA**; cada commit só sai com **COMMIT** (um por commit).
  Push logo após o commit autorizado.
- **Revisão antes do COMMIT**: um COMMIT só é atendido se o conteúdo do commit **passou por revisão antes**. Por
  isso, todo pedido de autorização traz, além dos arquivos e da mensagem, o **resultado da revisão**: o que foi
  revisado, os achados e as correções feitas. O que a revisão cobre depende do conteúdo:

  | Conteúdo | Revisão exigida |
  |---|---|
  | Documentação | releitura completa contra o enunciado e os demais documentos (consistência, lacunas, segurança); diagramas validados com o parser do Mermaid |
  | Código | testes verdes + `code-review`; também `security-review` quando tocar autenticação, sincronização, integração ou infraestrutura; achados corrigidos e testes rodados de novo |
  | Configuração (git, Docker, Compose) | conferência do que muda, do que é ignorado ou publicado, e de que nenhum segredo entra no repositório |

  Se algo mudar depois da revisão (mesmo uma linha), a parte alterada é revisada de novo antes do pedido. Se um
  COMMIT chegar para um conteúdo ainda não revisado, a revisão é feita primeiro: sem achados, o commit segue; com
  correções, o conteúdo corrigido volta para nova autorização.
- **Placar a cada etapa**: ao terminar uma etapa, a lista inteira de etapas (seção 3) é republicada com ✅ / ⏳ /
  ⬜, com o resultado da etapa.
- **Pronto = verificado**: uma etapa só é dada como concluída com os testes passando e a verificação descrita nela
  feita (build, teste automatizado e/ou execução real). Falha é relatada com a saída, não escondida.
- **Revisão = revisar e corrigir**: achados de revisão são corrigidos (com teste quando couber) antes de apresentar
  o resultado; o que depender de decisão vira pergunta.
- **Decisões fora do escopo**: decisão nova ou mudança de decisão é registrada no escopo antes de ser implementada.
- **Pendências** (decisões ou ações que dependem do Marcelo) vão para o painel (`add_pendencia`), nunca para
  arquivos no repositório.
- **Commits** em português, com mensagem descritiva, sem atribuição à IA. O uso de IA é declarado no README, como
  pede o enunciado.
- **Screenshots** de verificação em `.playwright-mcp/img/` (ignorado pelo git).

### Ferramentas de IA usadas

| Ferramenta | Uso |
|---|---|
| Claude Code (sessão principal) | implementação, testes, documentação |
| Skills nativas `code-review` e `security-review` | revisões nos marcos (etapas 4, 6, 8 e 10) |
| Skill nativa `run` | subir a aplicação para verificação |
| MCP **Playwright** (3 perfis isolados) | testes de aceitação no navegador: dois perfis simulam dois aparelhos (conflitos, isolamento entre usuários), o terceiro abre o painel do consumidor; modo offline via `setOffline`; páginas estáticas abertas por um servidor HTTP temporário em `127.0.0.1` (o navegador de teste bloqueia `file://`) |
| MCP **mobile** | **prova do offline no celular**: abrir a instância da VPS no Chrome de um Android (real ou emulado), instalar a PWA, criar e editar tarefas em modo avião, voltar a conexão e ver a sincronização e o painel do consumidor |
| MCP **Cloudflare** | criar o túnel e os hostnames públicos (com autorização) |
| MCP **context7** | documentação atual das bibliotecas (Dexie, vite-plugin-pwa, EF Core, Npgsql) |
| MCP **painel** | registro de pendências |
| Claude Design | geração das telas responsivas, revisadas e aprovadas pelo Marcelo |

---

## 2. Skills e agentes

Avaliação do que vale criar para o projeto. Critério: só criar o que for **repetido** e **não coberto** pelas
ferramentas nativas — criar e manter uma skill tem custo.

### Criar: skill `roteiro-demo` (recomendado)

Executa o **roteiro da demonstração** (escopo, seção 14) como **teste de aceitação ponta a ponta**, no navegador,
via MCP Playwright:

1. sobe ou confere o ambiente (`docker compose`) e os health checks;
2. perfil 1 = `ana` no "aparelho A", perfil 2 = `ana` no "aparelho B", perfil 3 = painel do consumidor;
3. percorre o roteiro: CRUD, tags, filtros, regra urgente (inclui "não é urgente" e conclusão), offline no
   aparelho A (`setOffline`), volta online, conflito entre A e B (título num, status no outro), isolamento com
   `bruno`, cadastro da integração, eventos no painel, modo de falha, reinício do consumidor e 205;
4. **parte mobile** (MCP mobile, contra a VPS): no Chrome do Android, login, instalação da PWA, modo avião,
   criação e edição de tarefas (inclusive uma urgente), fechar e reabrir o app sem rede, desligar o modo avião e
   conferir a sincronização no navegador do desktop e no painel do consumidor;
5. tira screenshots de cada passo em `.playwright-mcp/img/roteiro-demo/` e devolve o placar do roteiro.

Por que vale: o roteiro será executado várias vezes (fim da etapa 8, depois de cada correção relevante, na
conferência final e como **ensaio antes da apresentação**). Sem a skill, cada execução é refeita à mão e com
variações. Ela também gera o material visual do README.

Local: `.claude/skills/roteiro-demo/SKILL.md` (versionado — faz parte do processo declarado no README).
Criação: etapa 9, quando todas as peças existem.

### Não criar (e por quê)

| Candidato | Decisão |
|---|---|
| Agente "revisor de escopo" (confere invariantes: caminho único de escrita, sem libs de sync, paridade C#/TS, nenhuma credencial) | **não**: as invariantes estão no `CLAUDE.md`, que toda sessão e todo revisor carregam; a paridade C#/TS é garantida por teste (casos compartilhados). Um agente só repetiria isso |
| Skill "subir ambiente" | **não**: a skill nativa `run` + os comandos no `CLAUDE.md` bastam |
| Skill "validar diagramas Mermaid" | **não**: um passo da conferência final (etapa 10) com o parser oficial, feito uma vez |
| Agente para o frontend ou para a sync | **não**: são o centro da avaliação e precisam de uma única linha de raciocínio, na sessão principal |
| Workflow multiagente | **não por padrão**: o projeto é pequeno e sequencial. Fica disponível se o Marcelo pedir explicitamente |

### Uso de subagentes (nativos)

- **Consumidor simulado** (etapa 7): é independente do resto — depende só do contrato do evento (escopo,
  seção 6). Pode ser construído por um subagente em *worktree* separado enquanto a sessão principal faz o worker
  de webhooks. A sessão principal revisa e integra.
- **Revisões** nos marcos: `code-review` e `security-review` recebem o código (não as conclusões da sessão), para
  um olhar não ancorado na implementação.

---

## 3. Etapas

Placar. A coluna "IA" resume o que a IA faz; "Marcelo", o que é específico dele naquela etapa. Em **todas** as
etapas o Marcelo também conduz: define ou ajusta padrões, traz requisitos e soluções, questiona as propostas da IA,
pede revisões e pode mudar o rumo. O detalhe de cada etapa (entregas, critério de pronto, commits) está nas
subseções abaixo.

| # | Etapa | IA | Marcelo | Status |
|---|---|---|---|---|
| 0 | Documentação e revisão | analisa o enunciado; propõe alternativas e avalia as propostas do Marcelo; redige e revisa os documentos; valida os diagramas | define os padrões de trabalho; levanta requisitos e propõe soluções (integração, VPS, Cloudflare, regra urgente…); questiona e decide; pede revisões e aponta erros; COMMIT | ✅ documentação revisada e commitada |
| 1 | Telas no Claude Design (paralela às etapas 2–4) | gera as telas a partir do briefing (apêndice A) e aplica os ajustes pedidos | revisão, solicitação de ajustes e aprovação | ✅ telas aprovadas; marca documentada |
| 2 | Base do backend | implementa API, banco, seed e autenticação, com testes | preenche o `.env`; revisão; COMMIT | ✅ `0731b89` |
| 3 | Domínio e casos compartilhados | implementa merge, regra urgente e ordenação, guiado pelos casos | valida a tabela de casos da regra urgente; revisão; COMMIT | ✅ `e710508` |
| 4 | Sincronização no servidor | implementa push e pull com testes de integração; revisão de marco | revisão; COMMIT | ✅ `2ee7b8d` |
| 5 | Frontend base | implementa as telas e o banco local; confere no navegador | valida o visual; revisão; COMMIT | ✅ `ff6818e` |
| 6 | Motor de sincronização e PWA | implementa fila, sync, indicador e Service Worker; testa offline; revisão de marco | revisão; COMMIT | ⏳ implementada, testada e revisada; aguardando COMMIT |
| 7 | Integração externa e consumidor simulado | implementa a API de integrações e o worker; subagente faz o consumidor | revisão; COMMIT | ⬜ |
| 8 | Docker, Compose e VPS | escreve Dockerfiles e Compose; cria o túnel; revisão de marco | informa o domínio; autoriza o túnel; opera a VPS; revisão; COMMIT | ⬜ |
| 9 | Aceitação ponta a ponta (skill `roteiro-demo`) | cria a skill; executa no navegador e no celular; corrige | disponibiliza o Android; revisão; COMMIT | ⬜ |
| 10 | README final e conferência | escreve o README; faz a conferência completa; revisão final | revisa o README; revisão; COMMIT | ⬜ |
| 11 | Entrega | confere repositório e VPS; ensaio final | libera o acesso; envia o e-mail com as credenciais | ⬜ |

### Etapa 0 — Documentação e revisão
- **Entregas**: `escopo.md`, `arquitetura.md`, este plano, esqueleto do README, `CLAUDE.md`, `.gitignore`,
  `.gitattributes`.
- **IA**: leu e interpretou o enunciado; propôs a arquitetura com alternativas e recomendações (PostgreSQL com
  outbox transacional, LWW por campo, lock por usuário no push, tags em vez de listas, cookie `HttpOnly`, proteção
  contra SSRF); avaliou cada proposta do Marcelo contra o enunciado, apontando riscos e ajustes; redigiu os
  documentos; fez as revisões completas que ele pediu; validou os diagramas com o parser do Mermaid.
- **Marcelo**:
  - **padrões de trabalho**: portões DESENVOLVA e COMMIT; revisão obrigatória antes de qualquer commit; commits
    espaçados para o histórico refletir a evolução; credenciais fora do repositório, enviadas por e-mail;
    `.gitignore` e o que não deve ser versionado (`.vscode`, o PDF do desafio);
  - **requisitos e soluções que ele trouxe**: regra urgente que entende negação ("não é urgente"); concluídas no
    fim da lista; comportamento do logout com pendências; telas no Claude Design com premissa responsiva; publicação
    numa VPS com Docker Compose; Cloudflare (túnel/DNS) no lugar do Caddy, com dois subdomínios; consumidor com
    painel administrativo somente leitura e métricas; cadastro do consumidor por **um campo só**, no modelo do
    webhook do Portainer; par de URLs (entrada e visualização) sem login e regeneráveis; assinatura guardada no
    banco com painel de configuração no Way2Do; teste de resposta 200 antes de aceitar a URL; URLs que sobrevivem
    ao reinício do consumidor; retrato completo pedido pelo **código de resposta** do consumidor; link do painel na
    tela Integrações; prova do offline num celular real como obrigatória; controle de cache documentado;
  - **questionamentos que mudaram o desenho**: MongoDB × PostgreSQL (e a confusão sobre EF Core ser ORM); como
    cadastrar o consumidor sem acoplamento; se o pedido de retrato fere o "somente leitura" do enunciado; tags
    concorrentes; validação também no backend, não só no frontend; outbox simples, sem telas de acompanhamento;
  - **revisões**: pediu duas revisões completas contra o enunciado e o plano de execução; encontrou o diagrama
    Mermaid quebrado e a tabela do plano sem as colunas anunciadas;
  - **decisões** finais de produto, escopo e arquitetura; criação do repositório no GitHub.
- **Status**: ✅ concluída.
- **Pronto quando**: documentos revisados e commitados no GitHub.

### Etapa 1 — Telas no Claude Design (paralela)
- **Status**: ✅ concluída.
- **Entregas**:
  - telas responsivas (mobile-first) da seção 7 do escopo, aprovadas, em versão celular e desktop: 31 pranchetas no
    [design](https://claude.ai/artifact/2M9KXoB4gURw9v8Nv2CgJo) (privado; compartilhar pelo menu Share se for preciso);
  - [marca](marca.html): identidade visual, cores, contraste, tipografia, forma, ícones, componentes e tom de voz;
  - [design/way2do.css](design/way2do.css): tokens e componentes do design, base direta da etapa 5.
- **Como foi**: geração a partir do briefing; uma rodada de ajuste pedida pelo Marcelo (botões azul e vermelho
  fortes demais e com rótulo escuro): tons mais leves, rótulos brancos e correção na origem de um bug de
  especificidade no CSS, que também afetava o botão "Entrar de novo", o check de concluída e o negrito dos títulos;
  marca documentada em HTML a pedido do Marcelo. Integrações sem conexão: lista da última consulta com as ações
  desativadas, como prevê a seção 7 do escopo.
- **IA**: gera as telas no Claude Design a partir do briefing (apêndice A); aplica os ajustes pedidos, em rodadas,
  até a aprovação; na etapa 5, traduz o design aprovado para componentes React com CSS simples.
- **Marcelo**: revisa as telas, solicita ajustes e aprova.
- **Pronto quando**: as 9 telas/elementos do briefing aprovados pelo Marcelo, em versão celular e desktop.
- **Commits**: `docs: marca e tokens do design` (com o ajuste da seção 13 do escopo), após revisão.

### Etapa 2 — Base do backend
- **Entregas**: solução .NET (`Way2Do.Api`, `Way2Do.Api.Tests`), Compose com Postgres (`db`), EF Core +
  migration inicial (5 tabelas), seed de `ana` e `bruno` (senhas do `.env`), login/logout com cookie `HttpOnly`,
  checagem de `Origin`, rate limit com `ForwardedHeaders`, `/health`, `.env.example`.
- **IA**: implementa e testa; consulta a documentação atual via context7.
- **Marcelo**: preencher o `.env` local (ou usar o de `credenciais.local.md`); revisão; COMMIT.
- **Pronto quando**: `dotnet test` verde; login e logout testados (sucesso, senha errada com erro genérico,
  `Origin` inválido recusado, cookie com as flags corretas); API recusa subir em produção sem chave do JWT.
- **Como foi**: 16 testes de integração com Postgres real; um deles pegou o token sendo aceito também no cabeçalho
  `Authorization` (o JwtBearer volta ao cabeçalho quando não há cookie), corrigido. Execução real com Compose +
  `dotnet run` e `curl`. `Microsoft.Extensions.Identity.Core` removido (já vem no framework) e versões do EF
  alinhadas. O `.env` local foi criado pela IA a partir de `credenciais.local.md`. Versões das bibliotecas
  resolvidas pelo NuGet; o context7 não foi necessário. Novo: `GET /api/auth/me`.
- **Commits**: `feat(api): base da API, banco e autenticação`.

### Etapa 3 — Domínio e casos compartilhados
- **Entregas**: merge LWW por campo (inclui tags por item e exclusão vencendo), regra urgente com negação,
  ordenação, normalização de texto; casos compartilhados em `tests/fixtures/` (`urgencia.json`, `ordenacao.json`,
  `merge.json`).
- **IA**: implementa em C# primeiro, com os testes dirigidos pelos casos; a versão TS vem na etapa 5 contra os
  mesmos casos.
- **Marcelo**: revisar a tabela de casos da regra urgente (é regra de produto); revisão; COMMIT.
- **Pronto quando**: todos os casos da tabela do escopo (seção 4) e os cenários de conflito (seção 5) passam.
- **Como foi**: regra urgente, ordenação e merge em C# puro (`src/Way2Do.Api/Domain/`), sem banco; 28 casos de
  urgência (os 13 do escopo + 15 de borda), 2 cenários de ordenação e 20 de merge em JSON. A
  revisão de código achou que uma alteração inválida deixava a tarefa meio aplicada: o merge passou a validar tudo
  antes de escrever, e ganhou os limites de tamanho, título obrigatório na criação, criação imutável, exclusão de
  tarefa desconhecida como no-op, timestamps em milissegundos e normalização de texto equivalente à do JavaScript.
  Checagem de mutação: quebrar qualquer uma de 8 regras faz os testes falharem. A revisão de segurança não achou
  vulnerabilidades; três observações de convergência e robustez foram corrigidas (sigma final nas tags, título só
  com espaços invisíveis, caractere nulo e UTF-16 inválido). 100 testes no total.
- **Commits**: `feat(dominio): merge por campo, regra urgente e ordenação`.

### Etapa 4 — Sincronização no servidor
- **Entregas**: `POST /api/sync/push` (lock por usuário, validação e limites, resultado por operação, estado do
  servidor nas recusadas, outbox + entregas na mesma transação) e `GET /api/sync/pull` (cursor, paginação,
  tombstones e metadados de merge).
- **IA**: implementa; testes de integração com Testcontainers, incluindo o cenário de commits fora de ordem que
  justifica o lock e o de operação de outro usuário.
- **Marcelo**: revisão; COMMIT.
- **Revisão de marco**: `code-review` + `security-review` sobre as etapas 2–4; achados corrigidos.
- **Pronto quando**: testes de integração verdes; revisão sem achados abertos.
- **Como foi**: push transacional com lock consultivo por usuário, checagem de dono, merge validado, recusa com o
  estado do servidor, versão e evento uma vez por tarefa por lote, outbox e entregas por assinatura; pull com cursor,
  paginação e tombstones. Execução real: limite de 1 MB recusado com 413 pelo Kestrel. Revisão de marco (etapas
  2 a 4): a de segurança não achou vulnerabilidades; a de código achou 10 pontos, todos corrigidos, o principal
  sendo que um `null` no lote derrubava o push inteiro (500) e travaria a fila do aparelho. Agora cada operação é
  lida isoladamente; a recusa traz o estado final do lote; o mesmo id criado por dois usuários ao mesmo tempo é
  tratado; o relógio é lido depois do lock; e as tarefas do lote são carregadas numa consulta só. 25 testes de
  integração novos (125 no total, estáveis em 3 rodadas), incluindo a prova de que o push espera o lock do usuário
  e que pulls simultâneos a 20 pushes paralelos não perdem alterações. Checagem de mutação em 8 proteções (lock,
  dono, versão, filtro do pull, estado alheio na recusa, conflito de id, limite de 1 MB, campo nulo): todas
  detectadas pelos testes.
- **Commits**: `feat(sync): push e pull com merge, lock por usuário e outbox`.

### Etapa 5 — Frontend base
- **Entregas**: projeto Vite + React + TS em `src/web`; login; banco Dexie por usuário; lista ordenada, formulário,
  tags, filtros e busca; versão TS da regra urgente, ordenação e merge, testada contra os casos compartilhados;
  telas conforme o design aprovado, seguindo a [marca](marca.html) e os tokens de [design/way2do.css](design/way2do.css).
- **IA**: implementa e verifica no navegador via Playwright (celular e desktop, screenshots).
- **Marcelo**: validar o visual; revisão; COMMIT.
- **Pronto quando**: `npm test` verde (inclui os casos compartilhados); CRUD completo funcionando contra o banco
  local; layout conferido em largura de celular e de desktop.
- **Como foi**: domínio em TypeScript espelhando o C# e testado contra os mesmos JSON (paridade provada por teste,
  inclusive um arquivo novo de identidade de tag); banco Dexie por usuário com a operação enfileirada na mesma
  transação da escrita (a fila foi antecipada da etapa 6: nasce no mesmo ponto do código); telas do design aprovado
  em celular e desktop, conferidas no navegador (login, CRUD, filtros, busca sem acento, tags, exclusão, saída com
  pendências, isolamento entre usuários). Revisões: a de código achou 10 pontos (o principal: o formulário comparava
  com a versão atual da tarefa e desfazia edições de outro aparelho) e a de segurança achou um médio (o logout podia
  deixar o cookie válido); todos corrigidos, mais as observações (Vite liberando o repositório inteiro, tags
  `__proto__`/`constructor`, abas com usuários diferentes, `İ` divergindo entre C# e JS). 95 testes no frontend e
  135 no backend; checagem de mutação nas 5 correções principais. O indicador mostra só "Pendente (N)": os demais
  estados chegam com o motor de sincronização (etapa 6).
- **Commits**: `feat(web): telas, banco local e regras de ordenação`.

### Etapa 6 — Motor de sincronização e PWA
- **Entregas**: fila `pendingOps` na mesma transação da escrita; ciclo push → pull com merge; gatilhos e backoff;
  Web Locks (uma aba sincroniza); indicador com os 6 estados; tratamento de recusa; primeira sincronização;
  logout com pendências; Service Worker do app shell com aviso de nova versão.
- **IA**: implementa; testes com Vitest + `fake-indexeddb` (fila, merge na aplicação local, recusa, cursor que só
  avança); verificação no navegador com o modo offline do Playwright, incluindo **abrir o app sem rede**.
- **Marcelo**: revisão; COMMIT.
- **Revisão de marco**: `code-review` + `security-review` sobre as etapas 5–6; achados corrigidos.
- **Pronto quando**: criar/editar/concluir/excluir offline, recarregar a página offline, voltar online e ver tudo
  no servidor; conflito entre dois perfis resolvido por campo.
- **Como foi**: motor em `src/web/src/sync` (ciclo, gatilhos, backoff, Web Locks) testado contra um servidor em
  memória com o mesmo merge; no navegador, com o build de produção e o modo offline do Playwright: criar, concluir
  e excluir offline, **recarregar a página sem rede** (o app abre pelo Service Worker), voltar online e ver tudo no
  servidor; conflito entre dois perfis resolvido por campo (um concluiu, o outro renomeou: os dois ficaram
  concluídos e com o título novo); erro 503 simulado, sessão expirada com "Entrar de novo", recusa de operação
  adulterada, duas abas (só uma com o lock) e aviso de nova versão. Do backend veio um cabeçalho de conferência do
  usuário (observação de segurança deixada pela etapa 5). Revisão de código: 10 achados, 8 corrigidos no código (o
  principal: recusa de operação malformada voltava sem o estado do servidor e o aparelho apagaria a tarefa; também
  `AbortSignal.any`, que não existe no iOS anterior a 17.4) e 2 de documentação, resolvidos aqui. Revisão de
  segurança: nenhum achado. Checagem de mutação nas 6 correções principais. `npm audit` aponta 4 avisos de
  severidade baixa no `deepmerge`, usado só no build do Service Worker (não vai para o navegador).
- **Commits**: `feat(web): sincronização offline e PWA`.

### Etapa 7 — Integração externa e consumidor simulado
- **Entregas**: API — tela e endpoints de Integrações (validação anti-SSRF, ping, `dashboardUrl`, limite de
  assinaturas), worker de webhooks (SKIP LOCKED, backoff por 24 h, 205 com retrato limitado a 1/min).
  Consumidor — URLs derivadas por hash, multiespaço com limites, 200/205, painel somente leitura (sem XSS, com CSP),
  modo de falha.
- **IA**: sessão principal faz a parte da API; **subagente em worktree** faz o consumidor a partir do contrato; a
  sessão principal revisa e integra. Testes: validador anti-SSRF (tabela de IPs, redirecionamento), worker contra
  um consumidor de teste (retry, 205, intervalo).
- **Marcelo**: revisão; COMMIT.
- **Pronto quando**: cadastro com ping funcionando; alteração aparece no painel em menos de 1 s; modo de falha gera
  retry e o painel conta duplicados; reinício do consumidor → 205 → dados repostos.
- **Commits**: `feat(integracao): assinaturas, outbox e webhooks` e `feat(consumidor): consumidor simulado com painel`.

### Etapa 8 — Docker, Compose e VPS
- **Entregas**: Dockerfiles (sem root), `docker-compose.yml` (portas em `127.0.0.1`, healthchecks, variáveis
  obrigatórias), nginx com cache e cabeçalhos de segurança, `docker-compose.vps.yml` com `cloudflared`.
- **IA**: escreve e testa o Compose local; cria o túnel e os hostnames via MCP Cloudflare (com autorização);
  prepara os comandos da VPS.
- **Marcelo**: informar o domínio; autorizar a criação do túnel; executar o deploy na VPS (ou dar acesso);
  preencher o `.env` da VPS; revisão; COMMIT.
- **Revisão de marco**: `code-review` + `security-review` sobre as etapas 7–8; achados corrigidos.
- **Pronto quando**: `docker compose up --build` numa pasta limpa sobe tudo seguindo só o README;
  `https://way2do.<domínio>` e `https://way2do-consumer.<domínio>` funcionando; cabeçalhos de cache e segurança
  conferidos.
- **Commits**: `feat(infra): Docker, Compose local e implantação na VPS`.

### Etapa 9 — Aceitação ponta a ponta
- **Entregas**: skill `roteiro-demo`; execução completa do roteiro no ambiente local e na VPS; **prova do offline
  num celular Android** (real ou emulado) acessando a VPS.
- **IA**: cria a skill, executa (navegador via Playwright, celular via MCP mobile), corrige o que falhar, executa
  de novo.
- **Marcelo**: deixar um Android disponível para o MCP mobile (seção 6); revisão; COMMIT.
- **Pronto quando**: roteiro inteiro ✅ nos dois ambientes **e no celular**, com screenshots — inclusive o app
  abrindo sem rede no Android e as alterações feitas em modo avião chegando ao servidor e ao consumidor.
- **Commits**: `test: roteiro de aceitação ponta a ponta`.

### Etapa 10 — README final e conferência
- **Entregas**: README com as 4 partes, bibliotecas, uso de IA (a partir do registro da seção 5), roteiro da demo e
  pontos de destaque; diagramas atualizados e validados; `CLAUDE.md` com os comandos.
- **IA**: escreve; executa a conferência: clone limpo seguindo só o README, `dotnet list package --vulnerable`,
  `npm audit`, parser do Mermaid, busca por segredos no histórico do git, checklist "Antes de enviar".
- **Marcelo**: revisar o README (é a peça de comunicação avaliada); revisão; COMMIT.
- **Revisão de marco**: revisão final completa (código + documentos), achados corrigidos.
- **Pronto quando**: checklist inteiro ✅.
- **Commits**: `docs: README final`.

### Etapa 11 — Entrega
- **Marcelo**: adicionar os avaliadores como colaboradores e testar o acesso; preencher as URLs da VPS em
  `credenciais.local.md`; responder ao e-mail com "responder a todos", com o link do repositório e as credenciais,
  **no mínimo 2 horas antes** da apresentação.
- **IA**: conferir que o último commit está no GitHub e que a VPS está no ar; ensaio final com a skill
  `roteiro-demo`.
- **Antes da apresentação**: aplicação rodando (local e VPS), painel do consumidor aberto, roteiro à mão.

---

## 4. Dependências e paralelismo

```
Etapa 0 ──► 2 ──► 3 ──► 4 ──► 5 ──► 6 ──► 7 ──► 8 ──► 9 ──► 10 ──► 11
            ▲                 ▲            │
Etapa 1 ────┴─(paralela)──────┘            └─ consumidor em paralelo (subagente)
```

- A etapa 1 (Claude Design) foi planejada em paralelo às etapas 2–4 e acabou concluída antes do início do
  desenvolvimento; a etapa 5 já parte do design aprovado.
- Na etapa 7, o consumidor é construído em paralelo ao worker de webhooks.
- A prova no celular depende da VPS: fora de `localhost`, o Service Worker só funciona com HTTPS, que a VPS (via
  Cloudflare) fornece. Por isso a VPS é obrigatória, não um extra.
- Se o tempo apertar, vale a prioridade do enunciado: 2–6 antes de 7; o Compose local e a VPS da etapa 8 são
  obrigatórios; a skill `roteiro-demo` é a primeira a ceder (o roteiro, inclusive a prova no celular, é então
  executado manualmente com as mesmas ferramentas). O que ficar de fora vai para as limitações do README.

---

## 5. Registro de uso de IA

Preenchido ao fim de cada etapa; vira a seção "Uso de inteligência artificial" do README.

| Etapa | Como a IA ajudou | O que foi decidido ou revisado por pessoa |
|---|---|---|
| 0 | leitura do enunciado; propostas de arquitetura com alternativas e recomendação (PostgreSQL + outbox, LWW por campo, lock por usuário, tags, cookie `HttpOnly`, anti-SSRF, código 205); avaliação crítica das propostas do Marcelo; redação dos documentos; revisões de segurança e lacunas; validação dos diagramas | padrões de trabalho (portões, revisão antes de commit, credenciais fora do repositório); requisitos e soluções propostos por ele (negação na regra urgente, VPS com Cloudflare, consumidor com painel e métricas, cadastro por um campo só, par de URLs sem login, URLs que sobrevivem ao reinício, retrato pedido pelo código de resposta, prova no celular real); questionamentos que mudaram o desenho; revisões que ele pediu e erros que ele encontrou; todas as decisões finais |
| 1 | agente gerou as 31 pranchetas a partir do briefing; aplicou a rodada de ajuste e encontrou a causa do rótulo escuro (reset de CSS mais específico que as classes dos botões); recalculou o contraste de cada combinação; montou a página da marca a partir do CSS real e conferiu a renderização no navegador (celular e desktop) | briefing aprovado; revisão das telas; pedido de cores mais leves nos botões; pedido da marca como documento e em HTML; aprovação final |
| 2 | implementação da solução .NET, do modelo de dados e da autenticação; 16 testes de integração com Postgres real; os testes pegaram uma falha de segurança (token aceito também no cabeçalho `Authorization`) e ela foi corrigida; execução real verificada com `curl`; dependência desnecessária removida (`Identity.Core`, já no framework) | liberação com DESENVOLVA; padrões de segurança definidos na etapa 0 (cookie, credenciais fora do repositório); revisão e COMMIT pendentes |
| 3 | implementação do domínio guiada pelos casos; escrita dos casos de borda; checagem de mutação para provar que os testes detectam quebras | liberação da etapa; regra urgente com negação definida por ele na etapa 0; validação da tabela de casos e COMMIT pendentes |
| 4 | implementação de push e pull; testes de integração e de concorrência; checagem de mutação; revisão de marco das etapas 2 a 4 com correção dos 10 achados | liberação da etapa ("próxima"); revisão e COMMIT pendentes |
| 5 | implementação do frontend a partir do design aprovado; domínio em TS com paridade testada; verificação no navegador; revisões de código e de segurança com correção de todos os achados; checagem de mutação | liberação da etapa ("próxima"); validação do visual; COMMIT |
| 6 | motor de sincronização e PWA; testes com servidor em memória; verificação offline no navegador com dois perfis; revisão de marco (código e segurança) com correção dos achados; checagem de mutação | liberação da etapa ("continue"); COMMIT pendente |
| 7 | | |
| 8 | | |
| 9 | | |
| 10 | | |

---

## 6. O que preciso do Marcelo, e quando

| Quando | O quê |
|---|---|
| Agora | revisão e COMMIT dos arquivos da marca (etapa 1); **DESENVOLVA** para iniciar a etapa 2 |
| Etapa 3 | validar a tabela de casos da regra urgente |
| Etapa 8 | domínio para os hostnames; autorização para criar o túnel na Cloudflare; acesso ou execução na VPS |
| Até a etapa 9 | um **Android disponível para o MCP mobile**: celular ligado por USB com depuração USB ativa, ou emulador do Android Studio aberto (hoje não há nenhum conectado) |
| Cada etapa | COMMIT de cada commit (sempre depois de ver o resultado da revisão) |
| Etapa 11 | colaboradores no GitHub e envio do e-mail (pendências já no painel) |

---

## Apêndice A — Briefing para o Claude Design

> Desenhe as telas de um aplicativo web de lista de tarefas **offline-first**, usado principalmente **no celular,
> em campo, com conexão instável**, e também no desktop. **Responsivo e mobile-first**; uso confortável com uma
> mão. Visual limpo e funcional, sem sofisticação: clareza acima de estética. Idioma: português do Brasil.
>
> **Telas e elementos**
> 1. **Login** — usuário e senha; mensagem clara quando não há conexão ("o login precisa de internet").
> 2. **Indicador de sincronização** — sempre visível no topo, com 6 estados: *Offline · N pendentes*,
>    *Pendente (N)*, *Sincronizando*, *Sincronizado*, *Erro* (servidor com problema, tentando de novo),
>    *Sessão expirada* (com ação "entrar de novo"). Precisa ser legível de relance, com cor e ícone por estado.
> 3. **Lista de tarefas** — cada item mostra: título, selo **"urgente"** quando for o caso, prioridade (alta /
>    média / baixa), vencimento (destaque para vencidas), tags, e um controle para concluir/reabrir com um toque.
>    Tarefas urgentes ficam no topo; concluídas vão para o fim, esmaecidas. Estado vazio amigável.
> 4. **Filtros e busca** — status (todas / pendentes / concluídas), prioridade, tag e campo de busca. No celular,
>    recolhidos num painel; no desktop, visíveis.
> 5. **Tags** — lista das tags existentes para filtrar: lateral no desktop, painel no celular.
> 6. **Formulário de tarefa** — criar e editar: título (obrigatório), descrição, vencimento (data), prioridade,
>    tags (adicionar e remover). Excluir com confirmação. Tela cheia no celular, modal no desktop.
> 7. **Integrações** — lista das URLs cadastradas (com o token mascarado) e, quando houver, o link "abrir painel";
>    campo para colar uma nova URL, com estados de *testando*, *aceita* e *recusada (motivo)*; remover. Exige
>    conexão: offline, mostra aviso e desabilita as ações.
> 8. **Avisos** — "alteração recusada pelo servidor" e "nova versão disponível — atualizar".
> 9. **Logout** — confirmação com aviso quando há alterações não sincronizadas ("serão enviadas no seu próximo
>    login").
>
> Entregue cada tela em versão **celular** e **desktop**.
