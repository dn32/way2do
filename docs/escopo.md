# Escopo e decisões técnicas — Way2Do

Documento de trabalho: registra **o que** será construído, **como** e **por quê**. É a fonte do README final
(seções "Decisões técnicas e trade-offs", "Premissas" e "Limitações"). Os diagramas estão em
[arquitetura.md](arquitetura.md); a execução etapa por etapa, em [plano-de-execucao.md](plano-de-execucao.md).

---

## 1. Visão geral

Todo List web, usada no desktop e no celular, **offline-first**: toda leitura e escrita acontece primeiro no
banco local do navegador (IndexedDB); a sincronização com o servidor roda em segundo plano quando há conexão.
O servidor reconcilia as alterações e, só depois disso, propaga cada mudança para aplicações externas via webhook.

Três executáveis:

| Componente | Tecnologia | Papel |
|---|---|---|
| **Frontend** | React + Vite + TypeScript, PWA | UI, banco local, fila de sincronização, regra "urgente" |
| **API** | ASP.NET Core (.NET 10), Minimal APIs, EF Core | Auth, sincronização (push/pull), merge de conflitos, outbox, entrega de webhooks |
| **Consumidor simulado** | ASP.NET Core Minimal API | Recebe webhooks, registra e exibe o que recebeu, simula falhas |

Infra: **PostgreSQL** via Docker Compose.

---

## 2. Stack e justificativas

### Backend — ASP.NET Core 10 com Minimal APIs
- .NET é obrigatório; .NET 10 é a versão LTS atual.
- Minimal APIs em vez de controllers: menos cerimônia para uma API pequena (login, push, pull).
- **Um único projeto de API**, organizado por funcionalidade (`Auth/`, `Sync/`, `Webhooks/`, `Domain/`), em vez de
  Clean Architecture em várias camadas/projetos. Para o tamanho do problema, camadas extras seriam cerimônia sem
  ganho. A lógica de domínio crítica (merge e regra urgente) fica em classes puras, testáveis sem banco.

### Banco — PostgreSQL + EF Core
- Transações ACID resolvem os dois pontos centrais do desafio:
  - **Outbox transacional**: a alteração da tarefa e o evento da integração são gravados na mesma transação.
  - **Cursor de sincronização**: sequência global crescente (`bigint` via sequence) para o pull "o que mudou desde X".
- `FOR UPDATE SKIP LOCKED` permite que o worker de webhooks rode em mais de uma instância no futuro sem entregar duas vezes.
- Considerado e descartado: **MongoDB**. Transações com vários documentos (necessárias para o outbox) e change
  streams exigem replica set, o que aumenta a configuração. Também não tem auto-incremento para o cursor.
- EF Core (pacote `Npgsql.EntityFrameworkCore.PostgreSQL`): migrations e mapeamento com pouco código. Os trechos
  sensíveis (lock por usuário, sequence, claim de entregas) usam SQL explícito onde for mais claro.

### Frontend — React + Vite + TypeScript
- O enunciado prefere Angular; a escolha por React se justifica por domínio da stack, o que permite concentrar o
  esforço no que é avaliado (offline e sincronização), e pelo ecossistema maduro de PWA e IndexedDB.
- **Dexie** (wrapper de IndexedDB): transações, índices e `useLiveQuery` (a UI reage a qualquer alteração no banco
  local). Dexie é só armazenamento; **a sincronização é escrita do zero** (não usamos Dexie Cloud, RxDB, PouchDB nem
  similares), conforme pede o enunciado.
- **vite-plugin-pwa** (Workbox): gera o Service Worker que faz cache do *app shell*, para que a aplicação **abra**
  sem rede, não só continue funcionando com a aba já aberta. Não participa da sincronização de dados.
- Estado de UI: hooks do React + `useLiveQuery`. Sem Redux/Zustand: o IndexedDB **é** o estado da aplicação.

### Autenticação — JWT em cookie `HttpOnly`, usuários pré-cadastrados
- Login por usuário e senha (hash via `PasswordHasher` do ASP.NET Core Identity). A API emite um JWT e o entrega
  num **cookie `HttpOnly`, `Secure`, `SameSite=Strict`**, com caminho `/api` e validade de 7 dias. Todas as rotas de
  dados exigem o token e filtram pelo `userId` dele.
- **Por que cookie e não `localStorage`**: o JavaScript da página não consegue ler o token, então um XSS não
  consegue roubá-lo. Como app e API estão na mesma origem (o nginx encaminha `/api`), o cookie vai automaticamente,
  sem CORS. `localhost` é contexto seguro, então o cookie `Secure` funciona também no ambiente local.
- **CSRF**: `SameSite=Strict` impede o envio do cookie a partir de outros sites; como segunda barreira, as rotas
  que alteram estado (inclusive o login) conferem o cabeçalho `Origin` contra a lista `Auth:AllowedOrigins`.
  Requisição sem `Origin` segue: navegadores sempre o enviam em POST entre origens, então a ausência indica um
  cliente que não é uma página de outro site.
- JWT assinado com HS256 (algoritmo fixo); emissor, audiência e validade sempre validados.
- Login com erro **genérico** ("usuário ou senha inválidos"), sem revelar se o usuário existe.
- Offline o token não é usado (nada vai para a rede). Para abrir o app sem conexão, o cliente guarda no IndexedDB
  apenas os dados **não secretos** do usuário logado (id e nome), que indicam qual banco local abrir.
- Logout: `POST /api/auth/logout` apaga o cookie. O app esquece o usuário logado na hora (sem esperar a rede). Se o
  servidor não confirmar com sucesso (sem conexão, timeout ou erro, como um 502 com a API fora), uma marca fica no
  banco do aparelho (`way2do-session`, não no do usuário) e o pedido é reenviado ao abrir o app e quando a conexão
  voltar, para o cookie não continuar válido. Um login novo descarta a marca (o cookie já é outro).
- A sessão local é observada por todas as abas: login ou logout em uma aba vale para as outras, e nenhuma aba fica
  com um usuário diferente do cookie.
- Sem tela de cadastro nem recuperação de senha (fora de escopo pelo enunciado). Usuários de teste criados no seed:
  **`ana`** e **`bruno`**. Dois usuários permitem demonstrar o isolamento entre contas.
- **Autorização por recurso**: toda leitura e alteração confere o dono. Tarefas de outro usuário são recusadas no
  push, o pull filtra por usuário, e `DELETE /api/integrations/{id}` só remove assinatura do próprio usuário
  (responde 404 para as dos outros, sem revelar que existem).
- **Credenciais fora do repositório**: as senhas dos usuários de teste vêm de variáveis de ambiente
  (`Seed__Users__Ana__Password`, `Seed__Users__Bruno__Password`) definidas no `.env`, que não é versionado. O
  `.env.example` traz só os nomes das variáveis. As credenciais da demonstração são enviadas por e-mail aos
  avaliadores; quem rodar localmente também pode definir as próprias senhas.
- **Limite de tentativas** no login (rate limiter nativo do ASP.NET Core, por IP), contra força bruta. O IP real
  do cliente vem dos cabeçalhos de proxy (`CF-Connecting-IP` na VPS, `X-Forwarded-For` do nginx), aceitos **só**
  quando a requisição chega dos proxies conhecidos da rede do Compose (`ForwardedHeaders` com `KnownNetworks`).
  Sem isso, todo o tráfego pareceria vir do nginx e o limite bloquearia todos os usuários de uma vez — ou, se os
  cabeçalhos fossem aceitos de qualquer origem, seria contornado forjando o IP.
- Chave de assinatura do JWT com no mínimo 32 bytes. Em produção a API **não sobe** sem ela; no ambiente local,
  se ausente, gera uma aleatória na subida (seção 10).

### Testes
- Backend: **xUnit** para o domínio puro (merge, urgência, ordenação) e testes de integração da sincronização com
  **Testcontainers** (Postgres real em container — o Docker já é pré-requisito).
- Backend, integração: validador anti-SSRF (tabela de IPs aceitos e recusados, redirecionamento recusado) e worker
  de webhooks contra um consumidor de teste (retry com backoff, 205 → retrato completo, intervalo mínimo entre
  retratos).
- Frontend: **Vitest** + `fake-indexeddb` para o motor de sincronização e a regra urgente.
- Aceitação ponta a ponta: o roteiro da demonstração (seção 14) executado no navegador e num celular Android pela
  skill `roteiro-demo` (plano de execução, seção 2).
- **Casos compartilhados**: a regra "urgente", a ordenação e o merge existem no cliente (TS) e no servidor (C#). Os
  casos de teste ficam em JSON (`tests/fixtures/urgencia.json`, `ordenacao.json`, `merge.json` e `tags.json`) lidos
  pelas duas suítes, garantindo que as duas implementações concordam. Comparações de texto usam a ordem de código UTF-16, que é
  a nativa nas duas linguagens (`string.CompareOrdinal` e o `<` do JavaScript), sem depender de configuração regional.

### Bibliotecas e por quê
O enunciado pede a justificativa de cada biblioteca. Esta é a lista completa prevista; qualquer acréscimo durante
o desenvolvimento entra aqui com o motivo, e a tabela vai para o README.

| Biblioteca | Onde | Por quê |
|---|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | API | acesso ao Postgres com migrations; SQL explícito nos trechos sensíveis |
| `Microsoft.EntityFrameworkCore.Design` | API (só em desenvolvimento) | geração das migrations pela ferramenta `dotnet ef` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | API | validação do JWT (lido do cookie) |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | testes .NET | padrão do ecossistema .NET (o runner executa os testes no `dotnet test`) |
| `Testcontainers.PostgreSql` | testes .NET | Postgres real nos testes de integração (lock, sequence e `SKIP LOCKED` não existem em banco em memória) |
| `Microsoft.AspNetCore.Mvc.Testing` | testes .NET | sobe a API em memória (`WebApplicationFactory`) para testar os endpoints de ponta a ponta |
| `react`, `react-dom` | web | UI (justificativa da troca por Angular na seção 2) |
| `vite`, `typescript`, `@vitejs/plugin-react` | web | build rápido, tipagem e o plugin do React para o Vite |
| `@types/node` | web (só desenvolvimento) | tipos do Node para a configuração do Vite e para os testes que leem `tests/fixtures` |
| `dexie`, `dexie-react-hooks` | web | IndexedDB com transações e consultas reativas — **só armazenamento** |
| `vite-plugin-pwa` | web | gera o Service Worker do *app shell* com Workbox — não participa da sincronização |
| `vitest`, `fake-indexeddb`, `@testing-library/react`, `jsdom` | testes web | testes do motor de sync, da regra urgente e dos componentes sem navegador (`jsdom` simula o DOM) |

Sem bibliotecas de sincronização, de estado global (Redux etc.) nem de componentes visuais.

---

## 3. Modelo de dados

### Tarefa
| Campo | Tipo | Observação |
|---|---|---|
| `id` | UUID | **gerado no cliente** — permite criar offline sem colisão e torna o push idempotente |
| `title` | texto | obrigatório |
| `description` | texto? | |
| `dueDate` | data? | só data, sem hora |
| `priority` | `low` / `medium` / `high` | padrão `medium` |
| `status` | `pending` / `done` | |
| `tags` | mapa tag → `{ present, ts }` | mecanismo de agrupamento; cada tag tem o próprio estado e timestamp (ver abaixo) |
| `createdAt` | timestamp | |
| `deletedAt` | timestamp? | *tombstone* — exclusão lógica, necessária para propagar exclusões |
| `fieldTimestamps` | mapa campo → timestamp | base do merge por campo |
| `version` | inteiro | incrementa a cada alteração efetiva no servidor; vai nos eventos da integração |
| `changeSeq` | bigint | (só servidor) posição na sequência global; cursor do pull |

- `id` gerado com `crypto.randomUUID()` (disponível em contexto seguro: `localhost` ou HTTPS).
- `dueDate` trafega como texto `AAAA-MM-DD`, sem fuso: é uma data de calendário, não um instante.
- O pull devolve também `fieldTimestamps` e o mapa de tags completo (com as removidas), que o cliente precisa
  para o merge.

### Tabelas do servidor
| Tabela | Conteúdo |
|---|---|
| `users` | id, nome de usuário, hash da senha |
| `tasks` | campos da tarefa acima, `user_id`, `field_timestamps` e `tags` em `jsonb`; índice `(user_id, change_seq)` |
| `outbox_events` | id do evento, tipo, `task_id`, `user_id`, versão, corpo JSON pronto, data |
| `webhook_subscriptions` | id, `user_id`, URL de entrada, `dashboard_url`, `last_resync_at`, data; única por usuário + URL |
| `webhook_deliveries` | evento × assinatura: status (`pending` / `delivered` / `failed`), tentativas, `next_attempt_at`, último erro; índice `(status, next_attempt_at)` |

### Agrupamento — **tags**
Cada tarefa tem zero ou mais tags; a UI mostra a lista de tags existentes (derivada das tarefas) e permite filtrar
por uma delas.

Por que tags e não listas: listas seriam uma **segunda entidade sincronizada**, com conflitos próprios (ex.: lista
excluída em um aparelho enquanto outro cria tarefas nela offline). Tags ficam dentro da tarefa e reaproveitam o
mecanismo de sync. Trade-off: não há metadados de lista (cor, ordem, compartilhamento).

**Cada tag é tratada como um campo próprio no merge.** Se a lista de tags fosse um campo único, duas edições
concorrentes se sobrescreveriam: o celular offline adiciona `cliente`, o desktop adiciona `financeiro`, e uma das
duas se perderia. Por isso cada tag guarda `{ present, ts }` (adicionada ou removida, e quando):

- adicionar uma tag grava `present: true`; remover grava `present: false` — a entrada removida permanece no mapa
  como *tombstone*, para que uma remoção não seja desfeita por uma cópia antiga;
- o merge decide **tag por tag** pelo timestamp mais recente (mesma regra LWW dos demais campos); no exemplo, o
  resultado é `obra, cliente, financeiro`;
- empate exato de timestamp: a presença vence (a tag não some por acaso);
- identidade da tag: forma Unicode NFC, sem espaços nas pontas e em minúsculas (`Obra`, ` obra ` e a forma
  decomposta de `Ação` são a mesma tag); até 40 caracteres e até 20 tags visíveis por tarefa. As minúsculas são as
  mesmas nos dois lados: o `İ` turco vira `i` e o sigma final vira `σ` antes de comparar (as únicas diferenças entre
  o `toLowerCase()` do JavaScript e o `ToLowerInvariant()` do .NET). Casos em `tests/fixtures/tags.json`;
- para a UI, filtros e eventos da integração, as tags são a lista das entradas com `present: true`.

---

## 4. Regra "urgente" e ordenação

### Detecção
O enunciado pede que tarefas com a palavra "urgente" no título ou na descrição subam para o topo. Uma leitura
literal colocaria no topo "isto **não é urgente**", o oposto do que o usuário quis dizer. Por isso a regra
reconhece **as variações da palavra** e **ignora menções negadas**.

Algoritmo (determinístico, sem IA nem dicionário externo; idêntico em C# e TypeScript):

1. **Normaliza** o texto: minúsculas e remoção de acentos (NFD sem diacríticos). "Urgência" → "urgencia",
   "NÃO" → "nao". Em JavaScript: `texto.normalize('NFD').replace(/\p{Mn}/gu, '').toLowerCase().replace(/ς/g, 'σ')`;
   palavra = `[\p{L}\p{Nd}]`, por code point, nos dois lados.
2. **Separa em tokens**: palavras (sequências de letras/dígitos) e pontuação (qualquer outro símbolo, como
   `, . ; : ! ? ( ) - #`, e as quebras de linha). Espaços só separam.
3. **Termo de urgência**: toda palavra que **começa com `urgen`** — urgente, urgentes, urgência, urgências,
   urgentemente, urgentíssimo.
4. **Negação**: para cada termo, anda para trás **até 3 palavras**:
   - **palavra de negação** (não, nao, n, ñ, nem, sem, nunca, jamais, nada, nenhum, nenhuma) → menção negada;
   - **palavra de ligação** (e/é, eh, está, tá, tem, há, foi, era, será, seja, for, mais, muito, tão, tanto, tipo,
     de, da, do, um, uma) → pula e continua olhando;
   - **qualquer outra palavra ou pontuação** → para: a menção não é negada. A negação não atravessa vírgula,
     ponto nem uma palavra "de conteúdo".
5. A tarefa é **urgente se existir ao menos uma menção não negada** no título ou na descrição.

| Texto | Urgente? | Por quê |
|---|---|---|
| "Urgente: ligar para o cliente" | sim | termo sem negação |
| "É URGENTE" | sim | maiúsculas ignoradas |
| "#urgente revisar contrato" | sim | `#` é separador |
| "Preciso disso urgentemente" | sim | variação da palavra |
| "Tarefa com urgência" | sim | variação da palavra |
| "não é urgente" | não | negação + palavra de ligação |
| "tarefa não urgente" | não | negação imediata |
| "tarefa não tem urgência" | não | negação + palavra de ligação |
| "sem urgência" | não | "sem" é negação |
| "nada urgente por aqui" | não | "nada" é negação |
| "Não esquecer: urgente!" | sim | os dois-pontos interrompem a negação |
| "Não esquecer de ligar urgente" | sim | "ligar" não é palavra de ligação: a busca para antes do "não" |
| título "urgente", descrição "não é urgente" | sim | basta uma menção não negada |

Limites conhecidos (registrados no README): dupla negação ("não é que não seja urgente") e ironia não são
interpretadas; a lista de palavras é em português.

A urgência é **derivada** (calculada a partir do texto), não é um campo gravado. Assim não existe estado para
dessincronizar: uma tarefa editada offline sobe ou desce na hora, porque a ordenação é recalculada a cada
renderização a partir do banco local. Os casos da tabela acima, mais 15 casos de borda, formam o arquivo
`tests/fixtures/urgencia.json`, compartilhado entre as suítes de teste C# e TS.

### Ordenação padrão da listagem
1. **Pendentes antes de concluídas**: tarefa concluída vai para o fim, **mesmo que seja urgente** (já não exige
   ação; continua exibindo o selo "urgente").
2. **Dentro de cada grupo, urgentes primeiro** (regra do enunciado). Assim, mesmo filtrando só as concluídas, as
   urgentes continuam no topo daquela listagem.
3. **Vencimento mais próximo primeiro**; sem vencimento vai para o fim do grupo.
4. **Prioridade**: alta → média → baixa.
5. **Criação mais antiga primeiro**.
6. `id`, como desempate final (ordem determinística).

Essa é uma interpretação consciente do enunciado ("acima das demais tarefas"): concluir uma tarefa urgente a
tira do topo. Ela fica registrada nas premissas do README e é mostrada na demonstração.

Justificativa: entre tarefas igualmente urgentes, o que mais define "o que fazer primeiro" é o prazo; a prioridade
desempata prazos iguais ou ausentes; a data de criação dá uma ordem estável e previsível (FIFO); o `id` garante que
a ordem nunca "pisque" entre renderizações. O mesmo critério vale para as não urgentes.

---

## 5. Sincronização offline

### Princípios
- **O banco local é a fonte da verdade da UI.** A tela lê só do IndexedDB; a rede nunca bloqueia uma ação do usuário.
- **Toda escrita local gera uma operação na fila** (`pendingOps`), na mesma transação do IndexedDB que altera a tarefa.
- **Merge é idempotente**: reenviar a mesma operação não muda nada. Isso dá *at-least-once* seguro no push sem
  tabela de operações processadas.

### Banco local (Dexie), um por usuário (`way2do-<userId>`)
- `tasks` — estado local das tarefas (inclui `fieldTimestamps` e `deletedAt`).
- `pendingOps` — fila: `{ opId, taskId, changes: { campo: { value, ts } }, tags: { tag: { present, ts } }, deleted?, createdAt }`.
- `meta` — cursor do último pull, `clientId`, dados do usuário logado.

Separar por usuário evita que dois usuários no mesmo aparelho vejam os dados um do outro.

### Ciclo de sincronização (cliente)
0. **Primeira sincronização** (login num aparelho novo ou banco local vazio): pull completo a partir do cursor 0,
   paginado, antes de liberar a lista; depois disso, o app funciona offline com tudo o que o usuário já tinha.
1. **Push**: envia as operações pendentes em lote (`POST /api/sync/push`). Confirmadas pelo servidor, saem da fila.
2. **Pull**: pede as alterações desde o cursor (`GET /api/sync/pull?since=<cursor>`), paginado.
3. **Aplicação local**: cada tarefa recebida é mesclada com a local usando o **mesmo merge por campo** do servidor,
   de modo que uma edição feita durante o ciclo não é sobrescrita.
4. Atualiza o cursor.

Gatilhos: abertura do app, evento `online`, alguns segundos após cada alteração local (com debounce), retorno do
app ao primeiro plano (`visibilitychange`) e periodicamente (~30 s) enquanto online, para trazer alterações feitas
em outros aparelhos. Em falha de rede, novas tentativas com *backoff* exponencial.

`navigator.onLine` só serve de dica: o estado "offline" é confirmado pela falha real das requisições.

**Uma aba sincroniza por vez.** Com o app aberto em várias abas, cada uma teria o próprio motor de sync disputando
a fila e o cursor. O motor só roda dentro de um lock da **Web Locks API** (`navigator.locks`, nome
`way2do-sync-<userId>`); as outras abas apenas leem o IndexedDB, que é compartilhado, e a UI delas se atualiza
pelo `useLiveQuery`. O cursor só avança (grava-se o maior entre o atual e o recebido).

### Validação, limites e operações recusadas
- O servidor valida cada operação: título obrigatório e com até 200 caracteres, descrição até 4.000, até 20 tags de
  até 40 caracteres, enums válidos, data válida. Texto com caractere nulo ou UTF-16 inválido (que o Postgres não grava) também é
  recusado, em vez de virar erro 500 na gravação. O formulário aplica as mesmas regras, então uma recusa só acontece
  por defeito ou adulteração do cliente.
- Lote de push com até 500 operações e corpo de até 1 MB; o cliente divide filas maiores em vários lotes.
- A resposta do push traz o resultado **por operação**: `aceita` ou `recusada` (com o motivo). Para uma operação
  recusada, a resposta inclui o **estado atual da tarefa no servidor**; o cliente remove a operação da fila,
  **substitui** a cópia local por esse estado (sem merge, para não manter o valor inválido) e avisa o usuário.
  Sem isso, uma operação inválida ficaria na fila para sempre, travando a sincronização das demais.

### Indicador de estado (UI)
| Estado | Quando |
|---|---|
| **Offline** | sem rede ou servidor inalcançável; mostra quantas alterações aguardam envio |
| **Sincronizando** | ciclo de push/pull em andamento |
| **Sincronizado** | fila vazia e último ciclo bem-sucedido |
| **Pendente** | online, com alterações na fila aguardando o próximo ciclo |
| **Erro** | o servidor respondeu com erro (5xx); nova tentativa com backoff. Diferente de "Offline": há rede, o problema é do servidor |
| **Sessão expirada** | servidor respondeu 401; os dados locais são preservados até novo login |

### Conflitos — *last-write-wins* por campo
Cada campo da tarefa carrega o timestamp da sua última alteração (`fieldTimestamps`). No merge (servidor e
cliente usam o mesmo algoritmo), **para cada campo vence o valor com timestamp mais recente**.

- Ex.: no celular offline o usuário conclui a tarefa; no desktop, muda o título. Após sincronizar, a tarefa fica
  **concluída e com o título novo**. Com LWW por registro inteiro, uma das duas alterações seria perdida.
- **Tags**: cada tag é decidida individualmente (seção 3); adições concorrentes de tags diferentes se somam.
- **Exclusão vence edição**: uma vez com `deletedAt`, a tarefa não volta, mesmo que outra edição chegue depois.
  Como os ids são UUIDs gerados no cliente, não existe "recriar a mesma tarefa".
- **Relógio do cliente**: os timestamps vêm do aparelho. O servidor **limita timestamps no futuro ao horário do
  servidor**, para que um aparelho com relógio adiantado não vença todas as disputas para sempre.
- Empate exato de timestamp: vence o maior valor na ordem de código UTF-16, com nulo abaixo de qualquer valor, para
  que todos os aparelhos convirjam para o mesmo resultado em qualquer ordem de aplicação.
- **A alteração é validada inteira antes de qualquer escrita**: um valor inválido recusa a operação toda e a tarefa
  fica intocada (nunca "meio aplicada").
- **Data de criação** é definida na criação e não muda depois.
- **Excluir uma tarefa que a cópia nunca viu não cria nada** (ex.: criada e excluída offline antes de sincronizar).
- **Timestamps truncados em milissegundos**, a precisão do JavaScript, para que servidor e cliente comparem os mesmos
  valores.
- O merge distingue **mudança de conteúdo** (um valor visível mudou: nova `version` e evento da integração) de
  **mudança só de metadados** (ex.: o mesmo valor com timestamp mais novo: grava, mas não gera evento).
- Casos em `tests/fixtures/merge.json`.

### Logout com alterações pendentes
- Ao sair, se houver alterações na fila, o app **avisa**: "há N alterações não sincronizadas; elas serão enviadas
  no seu próximo login".
- O banco local do usuário **não é apagado**. Enquanto ninguém estiver logado, **nada é sincronizado**.
- No próximo login do mesmo usuário, o app reabre o banco dele e o ciclo de sincronização envia a fila normalmente.
- Outro usuário no mesmo aparelho usa o próprio banco (`way2do-<userId>`) e não enxerga esses dados.

### Servidor — push
Para cada lote, numa transação:
1. `pg_advisory_xact_lock(hashtextextended(<userId>, <semente>))` — serializa as escritas **por usuário** (usuários
   diferentes seguem em paralelo).
2. Para cada operação: garante que a tarefa pertence ao usuário do token (tarefa de outro usuário é recusada como
   "não encontrada", sem nenhum dado dela na resposta; dá para inferir que o id está em uso, sem impacto, porque
   o id é um UUID gerado no aparelho e impossível de adivinhar) ou cria, se não existe; aplica o merge por campo, que valida a
   operação inteira antes de escrever (regras e limites da seção "Validação, limites e operações recusadas").
3. Tarefa com mudança de conteúdo no lote: `version + 1` **uma vez por lote** (mesmo que várias operações tenham
   mexido nela) e **um evento no outbox** com o estado final: `todo.created`, `todo.updated` ou `todo.deleted`.
   Tarefa criada e excluída no mesmo lote não gera evento (o consumidor nunca a viu).
4. O `changeSeq` vem do banco: default no INSERT e trigger em todo UPDATE.
5. Commit.

Robustez do lote:
- **Cada operação é lida isoladamente**: operação malformada (nula, campo sem valor, data inválida, sem `taskId`)
  é recusada sozinha, sem derrubar o lote. Senão o aparelho reenviaria o mesmo lote para sempre.
- **A recusa traz o estado da tarefa ao fim do lote**, com `version` e `changeSeq` já gravados, e não um estado
  intermediário.
- **Mesmo id criado ao mesmo tempo por dois usuários** (locks de usuários diferentes não se bloqueiam): o INSERT
  duplicado é detectado e o lote é refeito; na nova tentativa a tarefa já existe, é de outro dono, e a operação
  é recusada.
- O relógio do servidor é lido depois do lock: o horário dos eventos acompanha a ordem de commit.
- As tarefas do lote são carregadas numa consulta só; lote vazio responde sem abrir transação.
- Corpo acima de 1 MB: 413 em ProblemDetails (o Kestrel aplica o mesmo limite a corpos sem `Content-Length`).

Por que o lock por usuário: sem ele, duas transações do mesmo usuário poderiam pegar `changeSeq` 10 e 11 e
commitar em ordem inversa; um pull entre os dois commits leria o 11, avançaria o cursor e **perderia o 10 para
sempre**. Com as escritas do usuário serializadas, a ordem da sequence é a ordem de commit, para cada usuário. O
custo é desprezível nessa escala (um usuário raramente tem dois pushes simultâneos).

### Servidor — pull
`SELECT ... WHERE userId = @u AND changeSeq > @cursor ORDER BY changeSeq LIMIT @n`, incluindo tombstones.
Responde com as tarefas e o novo cursor. Índice em `(userId, changeSeq)`.

---

## 6. Integração externa — outbox transacional + webhooks

### Assinaturas por usuário
Cada usuário cadastra, na tela **Integrações**, as URLs das aplicações externas que devem receber **as tarefas
dele**. Uma assinatura é **um campo só**: a URL de entrada fornecida pelo consumidor, que já contém o token de
acesso (no modelo dos webhooks do Portainer, Slack e Discord). O Way2Do não conhece nenhuma convenção interna do
consumidor.

- Por usuário, e não global: mantém o isolamento entre contas do enunciado ("cada usuário acessa apenas as suas
  próprias tarefas") e dispensa perfis de administrador. Vários usuários podem cadastrar a mesma URL.
- Assinaturas ficam no Postgres (`webhook_subscriptions`: `id`, `user_id`, `url`, `dashboard_url`,
  `last_resync_at`, `created_at`; única por usuário + URL).
- A tela exige conexão: cadastrar ou remover uma integração é uma ação de servidor e não entra na fila offline.
  **Só o backend faz push** para os consumidores; o dispositivo nunca fala com eles.
- A URL de entrada é um segredo: a API **devolve a URL mascarada** (só o host e o fim do token) e os logs também
  registram só a versão mascarada.
- Até **10 assinaturas por usuário** e limite de tentativas no cadastro: cada cadastro dispara uma requisição do
  servidor para fora (o `ping`), e sem limite a tela viraria um disparador de requisições para terceiros.

### Contrato com o consumidor
O contrato é o mesmo para qualquer consumidor, não só para o simulado:

| O consumidor responde | O Way2Do entende |
|---|---|
| **2xx** | entregue |
| **205 Reset Content** | entregue, **e** "perdi meu estado ou estou começando: mande o retrato completo" |
| 5xx, 429, timeout, erro de rede | não entregue; nova tentativa com backoff |
| 3xx (redirecionamento não é seguido) e outros 4xx | não entregue; nova tentativa com backoff (até o limite) |

Na resposta ao `webhook.ping`, o consumidor **pode** informar `{ "dashboardUrl": "..." }`: um endereço onde o
usuário acompanha os dados recebidos. É opcional; consumidores sem painel não informam nada.

### Cadastro de uma URL
Ao salvar, o **backend** (não só o frontend) valida, nesta ordem:
1. **Formato**: URL absoluta `http` ou `https`.
2. **Proteção contra SSRF**: o host é resolvido e o cadastro é recusado se algum IP for de loopback (127/8,
   ::1), "esta rede" (0/8), rede privada (10/8, 172.16/12, 192.168/16, fc00::/7), CGNAT (100.64/10), link-local
   (169.254/16, inclui o endpoint de metadados de nuvem; fe80::/10), multicast ou reservado — inclusive na forma
   IPv4 mapeada em IPv6 (`::ffff:10.0.0.1`). Regras complementares:
   - a checagem é refeita **a cada entrega**, no momento da conexão (`ConnectCallback` do `SocketsHttpHandler`),
     para que uma troca de DNS depois do cadastro (*DNS rebinding*) não contorne a regra;
   - **redirecionamentos não são seguidos** (3xx = falha): sem isso, uma URL pública poderia redirecionar para
     `169.254.169.254` e contornar a validação;
   - timeout de 5 s no cadastro e de 10 s nas entregas; a resposta do consumidor é lida até no máximo 16 KB.

   A configuração `Webhooks:AllowPrivateNetworks` libera a rede privada **apenas no ambiente local**, onde o
   consumidor é `http://consumer:8080`; na VPS fica desligada e cadastra-se a URL pública do consumidor.
3. **Teste de entrega**: o backend envia `webhook.ping` e só aceita o cadastro se receber **2xx** em até 5
   segundos. URL errada, token inválido ou consumidor fora do ar são recusados na hora, com a mensagem de erro.
4. **Link do painel**: se a resposta trouxer `dashboardUrl`, ele é gravado **somente se for `http`/`https`** (uma
   URL `javascript:` viraria XSS ao ser clicada). O servidor nunca acessa esse endereço; só o navegador do dono
   da assinatura, por um link com `rel="noopener noreferrer"`.
5. **Envio inicial**: se o ping recebeu **205**, o servidor enfileira o retrato completo (abaixo). Um consumidor
   novo responde 205 e recebe tudo; recadastrar uma URL que já tem os dados recebe 200 e não reenvia nada.

A remoção de uma assinatura cancela as entregas pendentes dela.

### Retrato completo (205 Reset Content)
Um único mecanismo cobre o **cadastro** e o **consumidor que reiniciou e perdeu o estado**: o consumidor responde
205 e o Way2Do enfileira um evento `todo.snapshot` para cada tarefa não excluída do dono da assinatura.

Por que isso **não é o consumidor escrevendo de volta na aplicação**:
- o consumidor não altera nenhuma tarefa nem nenhum dado visível aos usuários, e não faz nenhuma chamada ao
  Way2Do: apenas **responde** à requisição que recebeu;
- o código de resposta já governa a entrega (2xx encerra, 5xx reagenda); o 205 é mais uma regra desse protocolo;
- há precedente de mercado: nos REST Hooks do Zapier, o consumidor que responde **410 Gone** é descadastrado.

Condições que mantêm o mecanismo seguro:
- o retrato contém **só o que a assinatura já tem direito de receber** (as tarefas do dono dela);
- é **assíncrono**: entra na mesma fila do outbox; nada é enviado dentro da resposta;
- **no máximo um retrato por assinatura por minuto** (`last_resync_at`). Pedidos dentro do intervalo são agendados
  para o fim dele e agrupados, não descartados — um consumidor com defeito não consegue gerar reenvios em laço;
- eventos `todo.snapshot` nunca provocam outro retrato.

### Fluxo das alterações
1. O push de sincronização grava a alteração **e** o evento no outbox na mesma transação. Sem alteração efetiva,
   sem evento. Logo, alterações offline só viram evento **depois de chegar e ser reconciliadas no servidor**, como
   pede o enunciado.
2. Na mesma transação, cria uma **entrega** (`webhook_deliveries`) para cada assinatura do dono da tarefa.
3. Após o commit, a API sinaliza o worker (sinal em memória) para entregar imediatamente — latência de
   milissegundos. Um *polling* a cada poucos segundos cobre sinais perdidos e reinícios.
4. O **worker** (`BackgroundService`) reserva entregas pendentes com `FOR UPDATE SKIP LOCKED` e faz `POST` na URL.
5. Resposta conforme o contrato: 2xx → entregue; 205 → entregue e retrato enfileirado; falha → nova tentativa com
   **backoff exponencial com jitter** (1 s, 2 s, 4 s… até 5 min entre tentativas), por até **24 horas**. Depois
   disso, a entrega fica como `failed` (*dead-letter*), registrada no log. A janela longa cobre um consumidor fora
   do ar por horas sem perder alterações; se ele voltar vazio, o 205 repõe tudo.

Padrão outbox simples: sem tela de acompanhamento das entregas; o estado fica nas tabelas e nos logs.

### Evento
```json
{
  "eventId": "uuid",
  "type": "todo.created | todo.updated | todo.deleted | todo.snapshot | webhook.ping",
  "occurredAt": "2026-01-01T12:00:00Z",
  "todo": { "id": "...", "userId": "...", "userName": "ana", "version": 7, "title": "...", "...": "...", "deletedAt": null }
}
```
- O evento leva o **estado completo** da tarefa (*event-carried state transfer*), não só o que mudou.
- `todo.tags` vai como lista simples (só as tags presentes); `todo.urgent` vai calculado pelo servidor, para que
  o consumidor não precise reimplementar a regra.
- `webhook.ping` não traz `todo`.
- Cabeçalhos: `X-Way2Do-Event-Id` e `X-Way2Do-Event-Type`.

### Garantias
- **At-least-once**: nada se perde (evento gravado com a alteração; reentrega até confirmar).
- **Idempotência**: o consumidor descarta `eventId` já processado.
- **Ordenação**: tolerante a desordem por construção. Cada evento traz `todo.version` e o estado completo; o
  consumidor só aplica se `version` for maior que a que já tem. Assim, um evento atrasado por retry nunca
  sobrescreve um mais novo, e uma entrega em dead-letter não trava as seguintes. O mesmo vale para o retrato
  completo, que corre em paralelo com alterações novas. O worker ainda envia em ordem de `changeSeq`, por boa prática.
- **Recuperação do consumidor**: um consumidor que perdeu o estado pede o retrato com 205 e volta a ficar completo.
- **Isolamento de falhas**: cada assinatura tem as próprias entregas; um consumidor fora do ar não atrasa os outros.
- **Autenticidade**: o token secreto na URL (HTTPS na VPS). Assinatura HMAC do corpo fica como evolução (seção 12).

### Consumidor simulado
Minimal API .NET separada, publicada em `way2do-consumer.<domínio>`. Representa um sistema externo que
**apenas recebe** os dados (ex.: um painel gerencial ou BI).

**Princípio:** tudo o que o consumidor mostra vem **exclusivamente dos webhooks que ele recebeu** (a sua própria
projeção dos dados). Ele **não** acessa o banco nem a API do Way2Do e **não** escreve de volta. Se mostrasse dados
lendo direto da aplicação, a tela deixaria de provar que a integração funciona.

**URLs derivadas, sem login e sem segredo guardado.** Cada par de URLs nasce de um token aleatório `T` de 256 bits:

| URL | Forma | Quem usa | Permite |
|---|---|---|---|
| **Entrada** | `/in/<T>` | o Way2Do (é a URL cadastrada na tela Integrações) | enviar eventos |
| **Painel** | `/v/<SHA-256(T)>` | quem acompanha, no navegador | ver os dados e ligar/desligar o modo de falha do espaço (recurso de demonstração) |

- O consumidor **não guarda tokens**. Ao receber `/in/<T>`, calcula `SHA-256(T)` e grava no **espaço** com esse
  identificador; `/v/<H>` mostra o espaço `H`. Por isso **as URLs sobrevivem ao reinício**.
- A derivação é de mão única: quem tem a URL de entrada consegue calcular a do painel; quem tem a do painel não
  consegue descobrir a de entrada (não consegue injetar eventos).
- **Multiespaço**: qualquer token bem formado é aceito e vira um espaço isolado (como o webhook.site). Quem inventa
  um token só escreve no próprio espaço; adivinhar um token de 256 bits alheio é inviável. Token mal formado →
  **404**.
- Limites contra abuso: número máximo de espaços, de tarefas e de eventos guardados por espaço, e corpo de
  requisição de até 64 KB.
- A raiz `/` tem um botão **"gerar nova URL"**, que sorteia um `T` e mostra o par. Chaves novas a qualquer momento.
- O painel envia `Referrer-Policy: no-referrer`; o log de acesso não registra o caminho completo.
- **Sem XSS no painel**: o conteúdo das tarefas vem de fora (qualquer pessoa pode escrever num espaço que ela mesma
  criou) e é inserido na página só via `textContent`, nunca `innerHTML`; a página envia uma
  `Content-Security-Policy` restritiva (`default-src 'self'`, sem scripts inline).
- As URLs são montadas a partir de duas configurações, porque no ambiente local a API e o navegador enxergam o
  consumidor por endereços diferentes: `Consumer:IngestBaseUrl` (local: `http://consumer:8080`, usado pela API) e
  `Consumer:ViewBaseUrl` (local: `http://localhost:5200`, usado pelo navegador). Na VPS, as duas são
  `https://way2do-consumer.<domínio>`.

Comportamento ao receber:
- Espaço **vazio** (novo ou perdido num reinício) e evento que não é `todo.snapshot` → aplica o evento e responde
  **205**, pedindo o retrato completo. Inclui o `webhook.ping` do cadastro.
- Demais casos → deduplica por `eventId`, aplica a regra de versão, registra no log e responde **200**.
- A resposta ao `webhook.ping` traz `dashboardUrl` = `ViewBaseUrl` + `/v/<SHA-256(T)>`.

Endpoints:
- `GET /` — página neutra com o botão "gerar nova URL".
- `POST /in/<T>` — recebe eventos, conforme acima.
- `GET /v/<H>` — painel administrativo **somente leitura** (abaixo); `GET /v/<H>/state` — o mesmo conteúdo em
  JSON, consumido pela página.
- `POST /v/<H>/failure-mode` — modo de falha do espaço (taxa de erro ou "fora do ar") para demonstrar retry,
  backoff e recuperação ao vivo. Altera só o comportamento do próprio consumidor, não os dados do Way2Do.

Painel, atualizado automaticamente a cada poucos segundos (dados do espaço):

| Bloco | Conteúdo |
|---|---|
| **Tarefas recebidas** | tabela somente leitura com o estado atual de cada tarefa: título, usuário, status, prioridade, urgente, vencimento, tags, versão e última atualização; filtro simples por usuário e status |
| **Contadores** | total de tarefas; por status; por prioridade; urgentes pendentes; vencidas; excluídas; por usuário |
| **Saúde da integração** | eventos recebidos (por tipo); duplicados descartados (`eventId` repetido); fora de ordem descartados (versão antiga); retratos completos pedidos (205); latência média e máxima entre `occurredAt` e o recebimento; horário do último evento |
| **Modo de falha** | estado atual e quantas requisições foram recusadas de propósito |
| **Últimos eventos** | os 20 mais recentes, com tipo, tarefa, versão e resultado (aplicado / duplicado / desatualizado) |

Os contadores de duplicados e fora de ordem tornam **visíveis** as garantias de entrega: ao ligar o modo de falha,
o painel mostra as reentregas chegando e sendo tratadas sem efeito colateral. Reiniciar o consumidor na frente dos
avaliadores mostra o 205 e o retrato completo repondo os dados.

Implementação: HTML estático com um pouco de JavaScript puro (sem framework), servido pela própria Minimal API.
Estado **em memória**: a perda no reinício é coberta pelo retrato completo (205). Fica fora do Claude Design: é uma
tela de apoio à demo, não parte do produto.

---

## 7. Interface

As telas serão desenhadas no **Claude Design** a partir desta lista. Premissa obrigatória: **responsivo,
mobile-first** — o uso principal é no celular, em campo; no desktop, a mesma interface aproveita a largura.

| Tela / elemento | Conteúdo |
|---|---|
| **Login** | usuário e senha; mensagem clara quando não há conexão (o login exige rede) |
| **Lista de tarefas** | tarefas ordenadas (seção 4); selo "urgente"; prioridade, vencimento (destaque para vencidas, pela data local do aparelho) e tags; concluir/reabrir com um toque |
| **Filtros e busca** | status (todas / pendentes / concluídas), prioridade, tag e busca por texto no título e na descrição, ignorando maiúsculas e acentos (mesma normalização da regra urgente); no celular, recolhidos em um painel |
| **Tags** | lista das tags existentes para filtrar (lateral no desktop, painel no celular) |
| **Formulário de tarefa** | criar e editar: título (obrigatório), descrição, vencimento, prioridade, tags; excluir com confirmação; tela cheia no celular, modal no desktop |
| **Indicador de sincronização** | sempre visível no topo: Offline · Pendente (N) · Sincronizando · Sincronizado · Erro · Sessão expirada |
| **Avisos** | operação recusada pelo servidor (seção 5); "nova versão disponível — atualizar" (seção 10) |
| **Logout** | aviso de alterações não sincronizadas (seção 5) |
| **Integrações** | lista das URLs cadastradas pelo usuário (token mascarado), cada uma com o link **"abrir painel"** quando o consumidor informou um (`dashboardUrl`); adicionar (com a validação e o teste de entrega da seção 6, exibindo o erro se recusada) e remover; exige conexão — offline, mostra o aviso e desabilita as ações |

Sem design sofisticado (fora de escopo pelo enunciado): o foco é clareza e uso com uma mão no celular.

---

## 8. API

| Método | Rota | Descrição |
|---|---|---|
| `POST` | `/api/auth/login` | usuário e senha → cookie `HttpOnly` com o JWT + dados não secretos do usuário (id, nome) |
| `POST` | `/api/auth/logout` | apaga o cookie |
| `GET` | `/api/auth/me` | usuário da sessão atual (o app confere se a sessão ainda vale ao abrir online) |
| `POST` | `/api/sync/push` | lote de até 500 operações → resultado por operação (recusadas trazem o estado do servidor) |
| `GET` | `/api/sync/pull?since=&limit=` | alterações desde o cursor (`limit` de 1 a 1000, padrão 200) + novo cursor + `hasMore` |
| `GET` | `/api/integrations` | assinaturas do usuário logado, com a URL **mascarada** e o `dashboardUrl`, se houver |
| `POST` | `/api/integrations` | `{ url }` → valida, testa com `webhook.ping`, grava (com o `dashboardUrl` da resposta) e, se o ping recebeu 205, enfileira o retrato completo |
| `DELETE` | `/api/integrations/{id}` | remove a assinatura e cancela as entregas pendentes dela |
| `GET` | `/health` | health check |

Contrato do push (corpo JSON):

```json
{
  "operations": [
    {
      "opId": "uuid",
      "taskId": "uuid gerado no aparelho",
      "createdAt": "2026-10-29T10:00:00.000Z",
      "fields": { "title": { "value": "Urgente: trocar disjuntor", "ts": "2026-10-29T10:00:00.000Z" } },
      "tags": { "obra": { "present": true, "ts": "2026-10-29T10:00:00.000Z" } },
      "deletedAt": null
    }
  ]
}
```

Resposta: `{ "results": [ { "opId", "taskId", "status": "accepted" | "rejected", "field", "reason", "task" } ] }`.
Em uma recusa, `task` traz o estado atual da tarefa no servidor (ou `null` se o servidor não a tem: o aparelho
remove a cópia local). O pull devolve `{ "tasks": [...], "cursor", "hasMore" }`, cada tarefa com `tags` (incluindo
as removidas), `fieldTimestamps`, `version` e `changeSeq`.

A escrita de tarefas passa **só pelo push**: existe um único caminho de escrita e, portanto, um único lugar onde o
merge acontece. Filtros e busca rodam no cliente sobre o IndexedDB, porque precisam funcionar offline.

---

## 9. Dimensionamento para ~1000 usuários simultâneos

Estimativa de carga (premissas conservadoras):
- Pull periódico a cada 30 s → 1000 / 30 ≈ **33 req/s**.
- Uma alteração por usuário por minuto (alto para uma lista de tarefas) → ≈ **17 push/s**.
- Total: **~50 req/s**, cada uma com consultas indexadas e transações curtas.
- Webhooks: uma entrega por alteração e por assinatura do dono; com 1 ou 2 assinaturas por usuário, **~17–35
  POST/s** saindo do worker. O retrato completo é limitado a um por minuto por assinatura e tem o tamanho das
  tarefas de um único usuário.

Uma instância de ASP.NET Core (Kestrel) atende milhares de req/s; um PostgreSQL modesto faz milhares de
transações simples por segundo. **Uma instância da API e um Postgres sobram com folga**, sem cache distribuído,
broker de mensagens nem orquestrador.

Decisões que mantêm a porta aberta para escalar, sem custo hoje:
- API **stateless** (JWT): basta pôr mais instâncias atrás de um balanceador.
- Worker com `SKIP LOCKED`: várias instâncias podem rodar o worker sem entregar em duplicidade.
- Índices desde o início: `tasks(user_id, change_seq)` e `webhook_deliveries(status, next_attempt_at)`.
- Pool de conexões do Npgsql (padrão de 100 conexões por instância) mais que suficiente.

Deliberadamente **não** usados: Kafka/RabbitMQ (o outbox no Postgres cumpre o papel com um componente a menos),
Redis, Kubernetes, microserviços, SignalR.

---

## 10. Execução e implantação

Dois cenários, a partir do mesmo conjunto de imagens Docker:

### Local (avaliadores) — `docker compose up --build`
| Serviço | Imagem | Exposição |
|---|---|---|
| `db` | `postgres` oficial | `localhost:5433` (para inspeção) |
| `api` | build de `src/Way2Do.Api` | interna |
| `consumer` | build de `src/Way2Do.Consumer` | `localhost:5200` |
| `web` | build do frontend servido por **nginx**; o nginx também faz proxy de `/api` para a `api` | `localhost:5300` |

- Frontend e API na **mesma origem** (o nginx encaminha `/api`): sem CORS e sem URL de API embutida no build.
- `localhost` é contexto seguro para o navegador, então o Service Worker funciona sem HTTPS.
- Migrations aplicadas e usuários de teste criados automaticamente na subida da API, que só inicia depois que o
  Postgres estiver saudável (`healthcheck` + `depends_on: condition: service_healthy`).
- Portas publicadas **só em `127.0.0.1`** (ex.: `127.0.0.1:5433:5432`). O Docker, por padrão, publica em todas as
  interfaces e passa por cima do firewall do sistema (ex.: `ufw` no Linux): sem isso, o Postgres ficaria acessível
  na rede.
- Containers rodam **sem root**: usuário `app` das imagens oficiais do ASP.NET e `nginx-unprivileged` no `web`.

### Primeira execução (o que o avaliador precisa fazer)
1. Copiar `.env.example` para `.env`.
2. Preencher as variáveis obrigatórias: `POSTGRES_PASSWORD`, `Seed__Users__Ana__Password` e
   `Seed__Users__Bruno__Password` — com as senhas enviadas por e-mail ou com quaisquer valores.
3. `docker compose up --build` e abrir `http://localhost:5300`.

Variável obrigatória ausente → o Compose para com uma mensagem clara (`${VAR:?defina no .env}`), em vez de subir
com um erro obscuro. A chave do JWT é **opcional no local**: se ausente, a API gera uma aleatória na subida (as
sessões caem a cada reinício da API, o que é aceitável localmente). Na VPS (`ASPNETCORE_ENVIRONMENT=Production`)
ela é obrigatória e a API não sobe sem ela.

### VPS (apresentação) — `docker compose -f docker-compose.yml -f docker-compose.vps.yml up -d --build`
Docker Compose puro na VPS, exposto pela internet via **Cloudflare Tunnel**:

- O arquivo da VPS acrescenta o serviço **`cloudflared`** (`tunnel run` com o token do túnel no `.env`) e **remove
  todas as portas publicadas**. A VPS não abre porta nenhuma: o `cloudflared` abre uma conexão de saída até a
  Cloudflare, que recebe o tráfego HTTPS e o entrega pelo túnel.
- **TLS termina na Cloudflare**: sem certificado na VPS, sem Let's Encrypt, sem disputa das portas 80/443 com
  outros serviços da máquina. HTTPS é obrigatório para o Service Worker funcionar em um **celular real**.
- Hostnames públicos do túnel:

  | Hostname | Destino no Compose | Para quê |
  |---|---|---|
  | `way2do.<domínio>` | `http://web:80` | frontend e API (o nginx encaminha `/api`) |
  | `way2do-consumer.<domínio>` | `http://consumer:8080` | página do consumidor simulado, para a demonstração |

- O consumidor usa `way2do-consumer.<domínio>` (um nível) e não `consumer.way2do.<domínio>` (dois níveis): o
  certificado gratuito da Cloudflare cobre só `*.<domínio>`.
- Na VPS, a URL cadastrada é a **pública** do consumidor (`https://way2do-consumer.<domínio>/in/<token>`), porque
  a proteção contra SSRF recusa endereços de rede privada: o webhook sai da API, passa pela Cloudflare e volta
  pelo túnel, exatamente como chegaria a um consumidor externo de verdade. O mesmo hostname serve o painel
  (`/v/<token>`).
- Trade-off: dependência da Cloudflare para o acesso externo. Em troca, nenhuma porta exposta e nenhuma gestão de
  certificado. Alternativas consideradas: Caddy com Let's Encrypt e nginx com certificado de origem da Cloudflare
  (ambos exigem as portas 80/443 livres na VPS).

### Controle de cache (nginx do `web`)
Com a Cloudflare na frente e um Service Worker no navegador, há **três camadas de cache** (Cloudflare, cache HTTP
do navegador e cache do Service Worker). Sem regras explícitas, uma versão nova do app pode demorar a chegar aos
aparelhos, porque a Cloudflare guarda arquivos `.js` em cache por padrão, inclusive o `sw.js`. O nginx define:

| Recurso | `Cache-Control` | Motivo |
|---|---|---|
| `index.html` e rotas da SPA | `no-cache` | sempre revalida: aponta para os arquivos da versão atual |
| `sw.js`, `registerSW.js`, `workbox-*.js` | `no-cache` | o navegador precisa detectar o Service Worker novo |
| `manifest.webmanifest` | `no-cache` | nome, ícones e escopo da PWA atualizados |
| `/assets/*` (nomes com hash gerados pelo Vite) | `public, max-age=31536000, immutable` | o nome muda a cada build; pode ficar em cache para sempre |
| `/api/*` | `no-store` (definido pela própria API) | dados do usuário: nunca em cache compartilhado |

- A Cloudflare respeita o `Cache-Control` da origem; não é preciso criar regras no painel dela.
- O Service Worker faz *precache* apenas do *app shell* (HTML, JS, CSS e ícones do build). **Respostas de `/api`
  não são cacheadas pelo Service Worker**: os dados offline vêm do IndexedDB, nunca do cache HTTP.
- Atualização do app: quando um Service Worker novo é detectado, a UI mostra "nova versão disponível — atualizar".
  A troca não é automática, para não recarregar a página no meio de uma edição.
- Recursos da Cloudflare que reescrevem JavaScript (ex.: Rocket Loader) devem ficar desligados para esse hostname.

### Cabeçalhos de segurança (nginx do `web` e consumidor)
`Content-Security-Policy` restritiva (`default-src 'self'`; sem scripts inline; `frame-ancestors 'none'`),
`X-Content-Type-Options: nosniff` e `Referrer-Policy: no-referrer`. O React já escapa o conteúdo renderizado; a CSP
é a segunda barreira contra XSS. Mesmo com o token protegido no cookie `HttpOnly`, um XSS ainda poderia ler os
dados do IndexedDB e fazer requisições em nome do usuário. Para a CSP sem scripts inline funcionar, o registro do
Service Worker é feito por arquivo ou módulo (no `vite-plugin-pwa`, `injectRegister` diferente de inline).

### Desenvolvimento
Postgres via Compose (`docker compose up db`), API e consumidor com `dotnet run` e frontend com `npm run dev`
(o Vite encaminha `/api` para a API).

### Configuração
Segredos e parâmetros em `.env` (versionado só o `.env.example`, sem valores secretos): senha do Postgres, chave de
assinatura do JWT, **senhas dos usuários de teste**, token do Cloudflare Tunnel, `Webhooks:AllowPrivateNetworks`
(ligado só no local), `Auth:AllowedOrigins` e as URLs base do consumidor. Nenhuma credencial fica no repositório.

---

## 11. Premissas

- O login exige conexão; depois de logado, o app funciona offline sem prazo (o token não é usado offline). O token
  (validade de 7 dias) só é exigido para sincronizar; a validade longa cobre períodos em campo sem rede.
- Se o token expira offline, o usuário continua usando o app; a sincronização pausa e pede novo login, sem perder
  as alterações locais.
- Os relógios dos aparelhos estão razoavelmente corretos (sincronizados por NTP, como é o padrão em celulares).
- "Contém a palavra urgente" = qualquer variação da palavra (urgente, urgência, urgentemente…), ignorando
  maiúsculas e acentos, **desde que não esteja negada** ("não é urgente" não sobe). Detalhes na seção 4.
- Tarefas concluídas vão para o fim da lista mesmo quando urgentes: o topo é para o que ainda exige ação. Dentro
  das concluídas, as urgentes continuam primeiro.
- A autenticação é simples (usuários pré-cadastrados, sem cadastro nem recuperação de senha), como o enunciado
  permite; o que ela garante é o isolamento entre contas.
- "Somente quando houver conexão", na integração, refere-se ao trecho servidor → consumidor: consumidor
  indisponível gera novas tentativas, não perda.
- Exclusões são propagadas aos consumidores como `todo.deleted` com o último estado da tarefa.
- A integração é **por usuário**: cada usuário cadastra as URLs que recebem as tarefas dele. O enunciado não exige
  uma integração global, e a assinatura por usuário preserva o isolamento entre contas.
- Cadastrar ou remover uma integração exige conexão; não é uma operação offline.
- O consumidor pede o retrato completo respondendo **205 Reset Content**; isso **não** é escrever de volta na
  aplicação: ele só responde à entrega, sem alterar dados nem chamar o Way2Do (justificativa completa na seção 6).
  O mesmo mecanismo cobre o cadastro e o consumidor que reiniciou; não há botão de "reenviar tudo".
- O offline é demonstrado no modo offline do DevTools (ambiente local) **e** num celular Android acessando a
  instância na VPS, que tem HTTPS (exigência do navegador para o Service Worker fora de `localhost`).
- O banco local permanece no aparelho após o logout (seção 5); quem tiver acesso ao aparelho tem acesso a esses dados.

---

## 12. Fora de escopo e limitações

- Cadastro, recuperação de senha e refresh token.
- Sem revogação de sessão: um token emitido vale até expirar (7 dias), mesmo após o logout em outro aparelho.
  Evolução: lista de revogação ou token curto + refresh token.
- Relógio lógico híbrido (HLC) no lugar do relógio do aparelho para o LWW.
- Notificação em tempo real para outros aparelhos do mesmo usuário (SignalR); hoje chegam pelo pull periódico.
- Limpeza periódica de tombstones e do outbox já entregue.
- Autenticidade dos webhooks só pelo token na URL: o token pode aparecer em logs de proxy. Evolução: assinatura
  HMAC do corpo com um segredo por assinatura, sem segredo trafegando na URL.
- Sem tela de acompanhamento das entregas (pendentes, dead-letter, reprocessamento); o estado fica nas tabelas e nos logs.
- Consumidor simulado guarda os dados em memória: ao reiniciar, as URLs continuam válidas (são derivadas do token),
  e o retrato completo (205) repõe as tarefas; o histórico de eventos e as métricas do painel recomeçam do zero.
- Quem obtém a URL de entrada (ex.: por log de proxy) também consegue calcular a do painel e ver os dados recebidos.
- As URLs de entrada (segredos) ficam em texto puro no banco. Evolução: criptografá-las em repouso.
- O usuário **não é avisado** quando uma alteração dele perde uma disputa de LWW para outra mais recente; o valor
  simplesmente converge. Evolução: registrar e exibir os conflitos resolvidos.
- Suíte end-to-end automatizada dentro do repositório (rodando em CI). A aceitação ponta a ponta é feita pela skill
  `roteiro-demo`, que conduz o navegador (Playwright) e o celular (MCP mobile) — ver o plano de execução.

---

## 13. Estrutura do repositório

```
src/
  Way2Do.Api/            API ASP.NET Core (auth, sync, outbox, worker de webhooks)
  Way2Do.Consumer/       consumidor externo simulado
  web/                   frontend React + Vite (PWA)
tests/
  Way2Do.Api.Tests/      xUnit (domínio + integração com Testcontainers)
  fixtures/              casos compartilhados entre C# e TS (ex.: urgencia.json)
docs/
  escopo.md
  arquitetura.md
  plano-de-execucao.md
  marca.html             identidade visual (abrir no navegador)
  design/way2do.css      tokens e componentes do design aprovado
.claude/skills/
  roteiro-demo/          skill de aceitação ponta a ponta (navegador + celular)
Way2Do.slnx              solução .NET
dotnet-tools.json        ferramenta local `dotnet-ef` (migrations)
docker-compose.yml       execução local completa
docker-compose.vps.yml   acréscimos para a VPS (cloudflared, sem portas publicadas)
.env.example             nomes das variáveis, sem valores secretos
CLAUDE.md                instruções para o agente de IA
README.md
```

Não versionados: `.env` e `credenciais.local.md` (credenciais enviadas por e-mail).

### Portas locais
| Serviço | Porta |
|---|---|
| Frontend (Vite em dev / nginx no Compose) | 5300 |
| API | 5100 |
| Consumidor simulado | 5200 |
| PostgreSQL | 5433 |

(Portas fora do padrão para evitar conflito com outros projetos na máquina, ex.: 5173, 8080 e 5432.)

---

## 14. Ordem de execução

Resumo. A numeração oficial das etapas, os papéis, a participação da IA e o critério de "pronto" de cada uma
estão no [plano de execução](plano-de-execucao.md).

0. Documentação: escopo, arquitetura, plano de execução, esqueleto do README.
1. Telas no Claude Design (em paralelo às etapas 2–4).
2. Base do backend: solução .NET, Docker Compose com Postgres, EF Core + migrations, seed de usuários, login com
   cookie.
3. Domínio: merge por campo, regra urgente, ordenação, com os testes e os casos compartilhados.
4. Endpoints de sync (push com lock por usuário e outbox; pull com cursor) + testes de integração.
5. Frontend: login, Dexie, tela de tarefas (CRUD, tags, filtros, busca, urgente no topo).
6. Motor de sincronização no cliente + indicador de estado + PWA (Service Worker), com testes.
7. Integração: tela e endpoints de Integrações (validação anti-SSRF, `ping`, `dashboardUrl`), worker de webhooks
   (retry, 205 e retrato completo) e consumidor simulado (URLs derivadas, deduplicação, painel, modo de falha).
8. Dockerfiles, Compose local completo e Compose da VPS (Cloudflare Tunnel); subida na VPS.
9. Aceitação ponta a ponta: skill `roteiro-demo` no ambiente local, na VPS e num celular Android.
10. README final (4 partes + bibliotecas + uso de IA) e conferência: clonar o repositório numa pasta limpa e
    seguir **só o README**; `dotnet list package --vulnerable` e `npm audit`; checklist "Antes de enviar".
11. Entrega.

As telas (etapa 5) dependem dos desenhos do Claude Design (etapa 1); até lá, o motor de dados e de sincronização
avança independente da UI.

**Commits ao longo do desenvolvimento**: o enunciado avalia o histórico. Cada passo concluído (e testado) vira ao
menos um commit com mensagem descritiva, em vez de um commit único no final.

### Roteiro da demonstração (o que a apresentação pede)
1. Visão da arquitetura (diagramas) e principais decisões.
2. Login com `ana`; criar tarefas, tags, filtros e busca.
3. Regra urgente: criar "Urgente: ..." e ver subir; editar para "não é urgente" e ver descer; concluir e ver ir
   para o fim (premissas explicadas).
4. Offline no navegador (DevTools) **e num celular Android na VPS** (modo avião, app reaberto sem rede): criar,
   editar, concluir e excluir; indicador "Offline · N pendentes".
5. Voltar online: sincronização automática; o mesmo usuário em outro navegador recebe as alterações.
6. Conflito: a mesma tarefa alterada em dois aparelhos (título num, status no outro) → os dois valores preservados.
7. Integração: cadastrar a URL do consumidor (ping, envio inicial), abrir o painel pelo link, ver os eventos
   chegando; ligar o modo de falha e ver retry e duplicados descartados; reiniciar o consumidor e ver o 205 repor
   os dados.

Se o tempo apertar, a prioridade do enunciado manda: 2–3–4–5–6 antes de 7. O Compose local e a VPS (etapa 8) são
obrigatórios: o primeiro para o "como executar" do README, a segunda para a prova do offline no celular. A skill
`roteiro-demo` é a primeira a ceder (o roteiro passa a ser executado manualmente). O que ficar de fora vai para as
limitações do README, com a explicação de como seria feito.
