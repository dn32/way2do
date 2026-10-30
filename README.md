# Way2Do — Todo List offline-first

Lista de tarefas web (desktop e celular) que funciona **totalmente offline**: as alterações ficam no aparelho e
sincronizam sozinhas quando a conexão volta. O servidor reconcilia as alterações e as propaga, em quase tempo
real, para aplicações externas via webhooks.

- **Backend**: ASP.NET Core (.NET 10) + PostgreSQL
- **Frontend**: React + Vite + TypeScript (PWA, IndexedDB)
- **Integração**: outbox transacional + webhooks com retry
- **Consumidor externo simulado**: ASP.NET Core

Arquitetura e diagramas: [docs/arquitetura.md](docs/arquitetura.md) · Escopo detalhado: [docs/escopo.md](docs/escopo.md) · Plano de execução: [docs/plano-de-execucao.md](docs/plano-de-execucao.md) · Requisitos do enunciado → solução → verificação: [docs/rastreabilidade.md](docs/rastreabilidade.md) · Marca: [docs/marca.html](docs/marca.html) (abrir localmente no navegador)

---

## 1. Como executar

### Pré-requisitos
- Para **executar**: [Docker](https://www.docker.com/) com Docker Compose (nada mais).
- Para **desenvolver e rodar os testes**: também [.NET SDK 10](https://dotnet.microsoft.com/download) e
  [Node.js 22+](https://nodejs.org/).

### Passo a passo
1. Clone o repositório e entre na pasta.
2. Crie o arquivo de configuração a partir do modelo: `cp .env.example .env` (no Windows: `copy .env.example .env`).
3. Preencha no `.env` as três variáveis obrigatórias: `POSTGRES_PASSWORD` (qualquer valor),
   `Seed__Users__Ana__Password` e `Seed__Users__Bruno__Password` (as senhas enviadas por e-mail ou as suas).
   Opcional: `Jwt__SigningKey` (32 caracteres ou mais); sem ela, a API gera uma chave a cada subida e as sessões
   caem quando a API reinicia (o app mostra "Sessão expirada" e basta entrar de novo).
4. Suba tudo: `docker compose up --build`. Na primeira vez o build leva alguns minutos; a aplicação está pronta
   quando os quatro serviços aparecem como `healthy` em `docker compose ps`.
5. Abra http://localhost:5300 e entre com `ana` ou `bruno`.

Para ver a integração externa: abra http://localhost:5200, clique em **Gerar nova URL**, copie a **URL de entrada**
(`http://consumer:8080/in/…`, o endereço que a API enxerga dentro do Docker), cadastre-a na tela **Integrações** do
Way2Do e use o link **Abrir painel**.

As portas 5300, 5200 e 5433 precisam estar livres. `docker compose down` para tudo e mantém os dados (volume do
Postgres); `docker compose down -v` apaga também os dados.

### Usuários de teste
Os usuários `ana` e `bruno` são criados automaticamente na primeira subida, com as senhas definidas no `.env`
(trocar a senha no `.env` depois disso exige recriar o banco: `docker compose down -v`).
Nenhuma credencial fica no repositório: as senhas da demonstração foram enviadas por e-mail, e também é possível
definir as próprias no `.env`.

### Endereços (execução local com Docker Compose)
| Serviço | URL |
|---|---|
| Aplicação | http://localhost:5300 |
| API | http://localhost:5300/api (mesma origem da aplicação, pelo nginx) |
| Consumidor simulado | http://localhost:5200 |
| PostgreSQL | localhost:5433 (para inspeção) |

No modo de desenvolvimento (`dotnet run` e `npm run dev`), a API responde também direto em http://localhost:5100.

### Instância de demonstração
| Serviço | URL |
|---|---|
| Aplicação | https://way2do.startbit.com.br |
| Consumidor simulado | https://way2do-consumer.startbit.com.br |

As senhas de `ana` e `bruno` vão por e-mail. Na instância de demonstração, a URL de entrada gerada pelo consumidor
é a pública (`https://way2do-consumer.startbit.com.br/in/…`): o webhook sai da API, passa pela internet e volta,
como chegaria a um sistema externo de verdade. HTTPS permite instalar o app e usá-lo offline num celular real.

### Testes
```bash
dotnet test Way2Do.slnx               # API (sobe um Postgres próprio em container; precisa do Docker) e consumidor
cd src/web && npm ci && npm test      # frontend: domínio com os casos compartilhados, banco local, sincronização
```
As regras que existem nos dois lados (urgência, ordenação, merge e identidade das tags) são testadas contra os
mesmos casos, em `tests/fixtures/*.json`.

### Roteiro de demonstração
O roteiro da apresentação é também o teste de aceitação ponta a ponta, na skill
[`roteiro-demo`](.claude/skills/roteiro-demo/SKILL.md), executada com o Claude Code no navegador (três perfis: dois
"aparelhos" da mesma usuária e o painel do consumidor) e num celular Android real:

1. Login, criar tarefas com tags e prioridade, filtros e busca sem acento.
2. Regra urgente: "Urgente: …" sobe com o selo; "… não é urgente" desce; concluída vai para o fim.
3. Offline: criar, editar, concluir e excluir sem rede; indicador "Offline · N pendentes"; recarregar a página sem
   rede (o app abre pelo Service Worker).
4. Volta da rede: sincronização automática; o outro aparelho recebe sozinho.
5. Conflito: título alterado num aparelho e conclusão no outro, os dois offline → a tarefa fica concluída **e** com
   o título novo nos dois.
6. Isolamento: `bruno` não vê nada da `ana`.
7. Integração: gerar a URL no consumidor, cadastrar na tela Integrações, abrir o painel pelo link, ver o retrato e
   as alterações chegando; modo instável (reentregas descartadas como duplicadas), fora do ar e recuperação;
   reiniciar o consumidor e ver o 205 repor tudo.
8. Celular (Android, contra a instância de demonstração): instalar a PWA, ligar o modo avião, fechar o app e o
   navegador, **reabrir sem rede**, criar uma tarefa urgente e concluir outra, desligar o modo avião e ver as
   alterações chegarem ao navegador do desktop e ao painel do consumidor.

---

## 2. Decisões técnicas e trade-offs

Resumo das decisões; a versão completa, com as alternativas consideradas, está no [escopo](docs/escopo.md). Os
diagramas (visão geral, sincronização offline e integração externa) estão em [docs/arquitetura.md](docs/arquitetura.md).

### Stack e banco de dados
- **Backend em ASP.NET Core (.NET 10, LTS) com Minimal APIs**, num único projeto organizado por funcionalidade
  (`Auth/`, `Sync/`, `Webhooks/`, `Domain/`). Camadas extras (Clean Architecture em vários projetos) seriam
  cerimônia sem ganho para uma API deste tamanho; a lógica crítica (merge e regra urgente) fica em classes puras,
  testadas sem banco.
- **PostgreSQL com EF Core.** As transações resolvem os dois pontos centrais: o **outbox transacional** (a alteração
  e o evento da integração na mesma transação) e o **cursor de sincronização** (uma sequence global que dá a ordem
  das alterações). `FOR UPDATE SKIP LOCKED` permite mais de um worker sem disputar a mesma entrega (uma reentrega
  eventual é absorvida pela idempotência do `eventId`). MongoDB foi considerado e
  descartado: transações com vários documentos e change streams exigem replica set, e não há auto-incremento para
  o cursor.
- **Frontend em React + Vite + TypeScript** em vez de Angular (o desejável no enunciado): domínio da stack, para
  concentrar o esforço no que é avaliado (offline e sincronização), e ecossistema maduro de PWA e IndexedDB.
  **Dexie** é só o armazenamento (IndexedDB com transações e consultas reativas); **a sincronização foi escrita do
  zero**, sem Dexie Cloud, RxDB, PouchDB ou similares. O Service Worker (`vite-plugin-pwa`) faz o app **abrir** sem
  rede, não só continuar funcionando com a aba aberta.
- **Autenticação simples e segura**: usuários pré-cadastrados (`ana` e `bruno`, para mostrar o isolamento), JWT num
  cookie `HttpOnly` e `SameSite=Strict` (o JavaScript não lê o token), conferência do `Origin` contra CSRF e limite
  de tentativas de login. Toda leitura e escrita filtra pelo dono. O cookie é `Secure` na instância de demonstração
  (HTTPS); no Compose local, em `http://localhost`, ele sai sem `Secure`, porque o Safari descarta cookie `Secure`
  recebido por HTTP.

### Mecanismo de sincronização
- **O banco local é a fonte da verdade da interface.** A tela lê só do IndexedDB; a rede nunca bloqueia uma ação.
  Cada usuário tem o próprio banco local (`way2do-<id>`).
- **Toda escrita local grava a tarefa e uma operação na fila, na mesma transação.** A operação leva só os campos
  alterados, cada um com o horário da alteração.
- **Ciclo**: push da fila em lotes (até 500 operações e abaixo do limite de 1 MB do servidor), depois pull de tudo
  que mudou desde o cursor,
  paginado; cada tarefa recebida é **mesclada** com a local pelo mesmo algoritmo do servidor, então uma edição feita
  durante o ciclo não é sobrescrita. O cursor só avança.
- **Gatilhos**: abertura do app, volta da rede, 2 s depois de cada alteração local, volta ao primeiro plano e a cada
  30 s. Em falha, novas tentativas com espera crescente (base de 2 s dobrando até 1 min, com jitter entre a metade
  e o total).
- **Uma aba sincroniza por vez** (Web Locks API); as outras só leem o IndexedDB e mostram o mesmo estado.
- **Um único caminho de escrita no servidor** (`POST /api/sync/push`), sempre pelo merge. O push serializa as
  escritas de cada usuário com um lock consultivo do Postgres: sem isso, dois pushes concorrentes poderiam gravar a
  sequência fora da ordem de commit e um pull no meio perderia uma alteração para sempre.
- **Operação inválida** (só acontece por defeito ou adulteração do cliente; o formulário aplica as mesmas regras) é
  recusada sozinha, sem travar a fila: a cópia local volta para o estado do servidor e o usuário é avisado.
- **Indicador sempre visível**: Offline · Pendente (N) · Sincronizando · Sincronizado · Erro · Sessão expirada.

### Conflitos: *last-write-wins* por campo
- Cada campo carrega o horário da última alteração; no merge, **vence o valor mais recente de cada campo**. Se um
  aparelho conclui a tarefa e outro muda o título, a tarefa fica **concluída e com o título novo**. Com LWW pela
  tarefa inteira, uma das duas alterações seria perdida.
- **Tags** são decididas uma a uma: adições concorrentes de tags diferentes se somam; no empate exato de horário,
  a tag presente vence a removida.
- **Exclusão vence edição**: a tarefa excluída não volta, mesmo que outra edição chegue depois.
- Nos campos, empate de horário é desempatado pelo valor (ordem UTF-16), para todos os aparelhos convergirem para o mesmo
  resultado em qualquer ordem. Horários no futuro são limitados ao relógio do servidor, para um aparelho adiantado
  não vencer todas as disputas para sempre.
- **O mesmo algoritmo existe em C# e em TypeScript**, testado contra os mesmos casos em
  [`tests/fixtures/merge.json`](tests/fixtures/merge.json).
- Por que não CRDT ou transformação operacional: para os campos de uma tarefa (texto curto, status, data), LWW por
  campo dá o resultado que o usuário espera com uma fração da complexidade.

### Integração externa e garantias de entrega
- **Outbox transacional + webhooks.** O push grava a alteração **e** o evento na mesma transação, com uma entrega por
  integração do dono da tarefa. Logo, uma alteração feita offline só vira evento **depois de chegar e ser
  reconciliada no servidor**, e só o backend fala com os consumidores. Sem alteração efetiva, sem evento.
- Um **worker** (`BackgroundService`) reserva as entregas com `FOR UPDATE SKIP LOCKED` e faz o `POST`; é acordado por
  um sinal em memória logo após o commit (o evento chega ao consumidor em dezenas de milissegundos) e por polling,
  que cobre sinais perdidos e reinícios. Cada integração envia por conta própria: um consumidor lento ou fora do ar
  não atrasa os outros.
- **Garantias**: *at-least-once* (reentrega até receber 2xx, com espera crescente e jitter, por até 24 h; depois
  dead-letter registrado no log); **idempotência** pelo `eventId`; **ordem** tolerante por construção (o evento leva
  o estado completo e a `version`; o consumidor ignora versão antiga, então um evento atrasado nunca sobrescreve um
  mais novo).
- **Falhas do consumidor**: qualquer resposta fora de 2xx (3xx, 4xx, 5xx), timeout ou erro de rede vira nova
  tentativa; redirecionamento não é seguido. Um
  consumidor que perdeu o estado responde **205 Reset Content** e recebe o retrato completo das tarefas do dono da
  integração (no máximo um por minuto). Toda integração recém-cadastrada também recebe o retrato.
- **Somente leitura**: o consumidor só responde às entregas; não altera dados nem chama o Way2Do.
- **Segurança**: o cadastro valida a URL, recusa redes internas (anti-SSRF, inclusive IPv6 e IPv4 embutido) e faz um
  teste de entrega antes de gravar; a regra é conferida de novo a cada conexão, contra troca de DNS. Na instância de
  demonstração, rede privada e loopback são recusadas; no Compose local elas são liberadas
  (`Webhooks__AllowPrivateNetworks`), porque o consumidor responde em `http://consumer:8080`, na rede do Docker.
  Link-local (169.254/16, metadados de nuvem) e endereços reservados são recusados sempre. A URL de entrada é
  tratada como segredo: aparece mascarada na tela e nos logs.
- Por que não mensageria (Kafka, RabbitMQ): o outbox no Postgres dá as mesmas garantias para esta escala com um
  componente a menos para operar.
- **Consumidor simulado** ([`src/Way2Do.Consumer`](src/Way2Do.Consumer)): gera um par de URLs (entrada secreta e
  painel derivado por SHA-256, sem login), mostra as tarefas recebidas e a saúde da integração (duplicados, fora de
  ordem, retratos pedidos, latência) e tem um modo de falha para demonstrar o retry ao vivo.

### Regra "urgente" e ordenação
- Uma tarefa é urgente quando o título ou a descrição contém **"urgente" ou uma variação** (urgência,
  urgentemente…), sem diferenciar maiúsculas e acentos, **e a menção não está negada**: "não é urgente" e "sem
  urgência" não sobem. A negação precisa vir até 3 palavras antes do termo, separada dele só por palavras de
  ligação (é, está, tem, muito, tão…); outra palavra ou pontuação interrompe ("não preciso disso urgente" é
  urgente). Todos os casos estão em
  [`tests/fixtures/urgencia.json`](tests/fixtures/urgencia.json).
- A urgência é **calculada a partir do texto** no aparelho, não é um campo gravado: uma tarefa editada offline sobe
  ou desce na hora, sem depender do servidor.
- **Ordenação padrão**: pendentes antes de concluídas → urgentes primeiro dentro de cada grupo → vencimento mais
  próximo (sem vencimento no fim) → prioridade (alta, média, baixa) → criação mais antiga → id. Entre tarefas
  igualmente urgentes, o prazo é o que mais define o que fazer primeiro; a prioridade desempata; a criação dá uma
  ordem estável; o id garante que a lista nunca "pisque".

### Dimensionamento para 1000 usuários simultâneos
- Estimativa conservadora: pull a cada 30 s (≈ 33 req/s) + uma alteração por usuário por minuto (≈ 17 push/s) ≈
  **50 req/s**, com consultas indexadas e transações curtas; webhooks ≈ 17–35 POST/s.
- **Uma instância da API e um Postgres sobram com folga**: o Kestrel atende milhares de req/s e um Postgres modesto
  faz milhares de transações simples por segundo. Sem cache distribuído, broker, orquestrador ou microserviços.
- A porta para crescer fica aberta sem custo hoje: API sem estado (JWT), worker que roda em várias instâncias
  (`SKIP LOCKED`) e índices desde o início.

### Implantação
- **Docker Compose** para tudo (Postgres, API, consumidor e nginx servindo o frontend e encaminhando `/api` na mesma
  origem), com API, consumidor e nginx sem root e portas só em `127.0.0.1`.
- **Instância de demonstração** numa VPS, exposta por **Cloudflare Tunnel**: nenhuma porta aberta e HTTPS, que é
  exigido para o Service Worker funcionar num celular real.

---

## 3. Premissas

- **Login exige conexão**; depois disso o app funciona offline sem prazo. O token (7 dias) só é usado para
  sincronizar; se expirar offline, o app continua funcionando, a sincronização pausa e pede novo login, sem perder
  nada.
- **Relógios dos aparelhos razoavelmente corretos** (NTP, como é o padrão nos celulares): o LWW usa o horário do
  aparelho.
- **"Contém a palavra urgente"** inclui as variações da palavra e exclui as menções negadas ("não é urgente").
- **Tarefa concluída vai para o fim mesmo se urgente**: o topo é para o que ainda exige ação. Entre as concluídas,
  as urgentes continuam primeiro.
- **Agrupamento por tags**, não por listas: uma tarefa pode ter várias tags, e listas seriam uma segunda entidade
  sincronizada, com conflitos próprios.
- **Integração por usuário**: cada usuário cadastra as URLs que recebem as tarefas dele, preservando o isolamento
  entre contas. Cadastrar ou remover uma integração exige conexão.
- **"Somente quando houver conexão"**, na integração, vale para o trecho servidor → consumidor: consumidor fora do ar
  gera novas tentativas, não perda.
- **O 205 não é o consumidor escrevendo na aplicação**: ele só responde à entrega que recebeu, sem alterar dados nem
  chamar o Way2Do. O código de resposta já governa a entrega (2xx encerra, 5xx reagenda); o 205 é mais uma regra
  desse protocolo.
- **Exclusões** são propagadas como `todo.deleted`, com o último estado da tarefa.
- **Logout não apaga o banco local**: as alterações pendentes são enviadas no próximo login do mesmo usuário; outro
  usuário no mesmo aparelho usa o próprio banco e não vê esses dados.

---

## 4. Limitações e próximos passos

- **Sem revogação de sessão**: um token vale até expirar (7 dias), mesmo após o logout em outro aparelho. Com mais
  tempo: token curto com refresh token, ou lista de revogação.
- **O banco local continua no aparelho depois do logout** (é o que permite enviar as pendências no próximo login) e
  não é criptografado: quem tiver acesso ao aparelho consegue lê-lo. Próximo passo: a opção "sair e apagar os dados
  deste aparelho", liberada quando não houver alterações pendentes.
- **LWW depende do relógio do aparelho.** Com mais tempo: relógio lógico híbrido (HLC), que dispensa relógios
  corretos.
- **O usuário não é avisado quando a alteração dele perde uma disputa** de LWW; o valor apenas converge. Próximo
  passo: registrar os conflitos resolvidos e mostrá-los.
- **A sincronização roda enquanto o app está aberto** (em qualquer aba ou na PWA instalada): alterações feitas
  offline sobem na próxima vez que o app estiver aberto com rede. Próximo passo: Background Sync no Service Worker,
  onde o navegador oferecer.
- **Outros aparelhos recebem as alterações pelo pull periódico** (até ~30 s). Próximo passo: notificação em tempo
  real (SignalR ou SSE) disparando o pull na hora.
- **O app espera ~2 s depois de cada alteração antes de enviar** (agrupa digitações em sequência): do clique ao
  consumidor são ~2 s, embora do servidor ao consumidor seja ~0,03 s.
- **Regra urgente**: dupla negação ("não é que não seja urgente") e ironia não são interpretadas; as palavras são em
  português.
- **Autenticidade dos webhooks só pelo token na URL** (que pode aparecer em log de proxy). Próximo passo: assinatura
  HMAC do corpo com um segredo por integração. As URLs também ficam em texto puro no banco; o próximo passo é
  criptografá-las em repouso.
- **Sem tela de acompanhamento das entregas** (pendentes, dead-letter, reprocessar): o estado está nas tabelas e nos
  logs.
- **Sem limpeza periódica** dos registros de exclusão e do outbox já entregue. Próximo passo: um job que apaga o que
  passou de N dias.
- **Consumidor simulado em memória**: ao reiniciar, o retrato completo repõe as tarefas, mas o histórico de eventos e
  as métricas do painel recomeçam do zero. Quem tem a URL de entrada também consegue calcular a do painel.
- **Aceitação ponta a ponta fora do CI**: o roteiro é executado pela skill `roteiro-demo` (navegador e celular), não
  por uma suíte end-to-end automatizada no pipeline. Próximo passo: Playwright no CI contra o Compose.
- **Cadastro e recuperação de senha** ficaram fora, como o enunciado permite.

---

## Bibliotecas e por quê

| Biblioteca | Onde | Por quê |
|---|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | API | acesso ao Postgres com migrations; SQL explícito nos trechos sensíveis (lock, reserva das entregas) |
| `Microsoft.EntityFrameworkCore.Design` | API (desenvolvimento) | geração das migrations pela ferramenta `dotnet ef` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | API | validação do JWT (lido do cookie) |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | testes .NET | padrão do ecossistema .NET |
| `Testcontainers.PostgreSql` | testes .NET | Postgres real nos testes de integração (lock, sequence e `SKIP LOCKED` não existem em banco em memória) |
| `Microsoft.AspNetCore.Mvc.Testing` | testes .NET | sobe a API e o consumidor em memória para testar os endpoints de ponta a ponta |
| `react`, `react-dom` | web | interface (motivo da troca por Angular acima) |
| `vite`, `typescript`, `@vitejs/plugin-react` | web | build rápido, tipagem e o plugin do React |
| `@types/node`, `@types/react`, `@types/react-dom` | web (desenvolvimento) | tipos do Node (configuração do Vite e testes que leem `tests/fixtures`) e do React |
| `dexie`, `dexie-react-hooks` | web | IndexedDB com transações e consultas reativas — **só armazenamento** |
| `vite-plugin-pwa` | web | gera o Service Worker do *app shell* — não participa da sincronização |
| `vitest`, `fake-indexeddb`, `@testing-library/react`, `jsdom` | testes web | motor de sincronização, regras e componentes testados sem navegador |

Sem bibliotecas de sincronização, de estado global (Redux etc.) nem de componentes visuais. O consumidor simulado
não usa nenhum pacote além do ASP.NET Core.

---

## Testes

| Suíte | O que cobre |
|---|---|
| API (xUnit + Postgres real) | domínio puro (merge, urgência, ordenação, tags) contra os casos compartilhados; autenticação e isolamento; push e pull (lote, recusas, concorrência, lock por usuário); validador anti-SSRF; worker de webhooks contra um consumidor HTTP de verdade (retry, 205, retrato, intervalo, duas instâncias ao mesmo tempo) |
| Consumidor (xUnit) | derivação das URLs, 205/200, deduplicação, regra de versão, limites, modo de falha, cabeçalhos de segurança |
| Frontend (Vitest) | as mesmas regras contra os mesmos casos JSON; banco local e fila; motor de sincronização contra um servidor em memória (offline, conflito, recusa, primeira sincronização paginada, agendamento) |
| Aceitação | o roteiro da demonstração, no navegador (local e instância de demonstração) e num Android real |

As correções mais delicadas passaram por **checagem de mutação**: a correção foi desfeita de propósito para
confirmar que algum teste falha sem ela.

---

## Uso de inteligência artificial

O desenvolvimento foi feito com o **Claude Code** (Anthropic) como par de programação, num processo com portões
definidos por mim: a IA propõe e implementa; eu defino os padrões, decido, reviso e aprovo cada commit.

- **Desenho**: a IA leu o enunciado, propôs a arquitetura com alternativas e recomendação (PostgreSQL + outbox, LWW
  por campo, lock por usuário, cookie `HttpOnly`, anti-SSRF) e redigiu os documentos. Várias decisões vieram de mim
  e mudaram o desenho: a negação na regra urgente, a demonstração na VPS com Cloudflare, o consumidor com painel e
  métricas, o cadastro por um campo só, o par de URLs sem login que sobrevive ao reinício, o retrato completo pedido
  pelo código de resposta e a prova num celular real.
- **Telas**: geradas no Claude Design a partir de um briefing, com revisão e ajustes meus; a página da marca
  ([docs/marca.html](docs/marca.html)) foi montada a partir do CSS real.
- **Implementação em etapas** ([plano de execução](docs/plano-de-execucao.md)), cada uma liberada por mim, com
  testes, verificação no navegador e **revisões de código e de segurança** antes de cada commit; os achados foram
  corrigidos e, quando mudavam uma regra combinada, decididos por mim. O consumidor simulado foi feito por um
  subagente numa worktree isolada, só a partir do contrato, e revisado antes de entrar.
- **Infraestrutura**: túnel e DNS criados pelo MCP da Cloudflare e implantação na VPS por SSH, com minha autorização
  e com os segredos gerados na própria máquina.
- **Aceitação**: a skill [`roteiro-demo`](.claude/skills/roteiro-demo/SKILL.md) executa o roteiro da demonstração no
  navegador (Playwright) e num Android real (MCP mobile).

O registro etapa a etapa, separando o que a IA fez do que foi decidido ou revisado por mim, está na seção 5 do
[plano de execução](docs/plano-de-execucao.md). Conheço e defendo cada decisão e cada trecho do código entregue.
