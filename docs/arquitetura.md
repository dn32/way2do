# Arquitetura — Way2Do

Diagramas em Mermaid (renderizados pelo GitHub). As decisões e justificativas estão em [escopo.md](escopo.md); a
execução etapa por etapa, em [plano-de-execucao.md](plano-de-execucao.md).

---

## 1. Visão geral

Quais são os componentes, onde cada um roda e como se comunicam.

```mermaid
flowchart LR
    subgraph Dispositivo["Dispositivo do usuário (navegador — desktop ou celular)"]
        UI["UI React"]
        SW["Service Worker<br/>(cache do app shell)"]
        IDB[("IndexedDB (Dexie)<br/>tasks · pendingOps · meta")]
        SYNC["Motor de sincronização"]
        UI -- "lê e grava<br/>(sempre local)" --> IDB
        SYNC -- "fila e cursor" --> IDB
        SW -. "serve o app<br/>sem rede" .-> UI
    end

    CF["Cloudflare (só na VPS)<br/>HTTPS · way2do.seudominio"]

    subgraph Servidor["Servidor (Docker Compose — local ou VPS)"]
        TUNNEL["cloudflared (só na VPS)<br/>túnel de saída, sem portas abertas"]
        WEB["nginx<br/>arquivos do frontend<br/>+ proxy de /api<br/>+ Cache-Control e cabeçalhos de segurança"]
        API["API ASP.NET Core<br/>Auth · Sync (push/pull) · Merge · Integrações"]
        TUNNEL --> WEB
        WEB -- "/api" --> API
        WORKER["Worker de webhooks<br/>(BackgroundService)"]
        PG[("PostgreSQL<br/>users · tasks · outbox_events<br/>webhook_subscriptions · webhook_deliveries")]
        API -- "transação:<br/>tarefa + evento" --> PG
        WORKER -- "reserva entregas<br/>SKIP LOCKED" --> PG
        API -. "sinal em memória<br/>(novo evento)" .-> WORKER
    end

    subgraph Externos["Aplicações externas (somente leitura)"]
        C1["Consumidor simulado<br/>(container no mesmo Compose)"]
        C2["Outros consumidores"]
    end

    SYNC -- "HTTPS + cookie com JWT<br/>POST /api/sync/push · GET /api/sync/pull" --> CF
    UI -- "POST /api/auth/login<br/>/api/integrations (online)" --> CF
    SW -. "baixa o app<br/>(online)" .-> CF
    CF -. "túnel" .-> TUNNEL
    TUNNEL -. "way2do-consumer.seudominio<br/>/in/T (webhooks) · /v/SHA-256(T) (painel)" .-> C1
    WORKER -- "POST /in/T<br/>local: rede interna · VPS: URL pública" --> C1
    API -. "webhook.ping no cadastro<br/>(resposta: 200/205 + dashboardUrl)" .-> C1
    WORKER -- "POST webhook" --> C2
```

| Componente | Onde roda | Comunica com |
|---|---|---|
| UI React + Service Worker | navegador | IndexedDB (local); API só para login, Integrações (online) e via motor de sync |
| IndexedDB (Dexie) | navegador, um banco por usuário | — |
| Motor de sincronização | navegador, uma aba por vez (Web Locks) | API (push/pull) quando há conexão |
| Cloudflare | borda da Cloudflare, **só na VPS** | recebe HTTPS e entrega pelo túnel |
| cloudflared | container, **só na VPS** | conexão de saída até a Cloudflare; repassa para o nginx e para a página do consumidor |
| nginx | container | serve o frontend com `Cache-Control` por tipo de arquivo e cabeçalhos de segurança (CSP); encaminha `/api` para a API (mesma origem, sem CORS) |
| API ASP.NET Core | container (ou `dotnet run` em dev) | PostgreSQL; consumidor, só no `webhook.ping` do cadastro de uma integração |
| Worker de webhooks | mesmo processo da API | PostgreSQL; consumidores via HTTP, nas URLs cadastradas por cada usuário |
| PostgreSQL | container | — |
| Consumidor simulado | container separado | recebe eventos em `/in/<T>`; painel somente leitura em `/v/<SHA-256(T)>`; um espaço isolado por token |

No ambiente local não há Cloudflare nem `cloudflared`: o navegador acessa o nginx direto em `localhost:5300`.

---

## 2. Fluxo da sincronização offline

Da gravação local no dispositivo até a reconciliação no servidor.

```mermaid
sequenceDiagram
    autonumber
    actor U as Usuário
    participant UI as UI React
    participant DB as IndexedDB
    participant S as Motor de sync (aba com o Web Lock)
    participant API as API
    participant PG as PostgreSQL

    Note over S,PG: primeiro acesso no aparelho: pull completo desde o cursor 0, paginado, antes de liberar a lista
    Note over U,DB: Offline — tudo acontece localmente
    U->>UI: cria/edita/conclui/remove tarefa
    UI->>DB: transação: altera tarefa + grava op em pendingOps<br/>(timestamp por campo alterado)
    DB-->>UI: useLiveQuery re-renderiza<br/>(urgentes recalculadas e no topo na hora)
    UI-->>U: indicador "Offline · N pendentes"

    Note over S,API: Conexão volta (evento online, timer ou app em primeiro plano)
    S->>DB: lê lote de pendingOps
    S->>API: POST /api/sync/push (lote + cookie com JWT)
    API->>PG: BEGIN + pg_advisory_xact_lock(usuário)
    loop cada operação
        API->>PG: carrega tarefa do usuário (ou cria)<br/>tarefa de outro usuário → recusada
        API->>API: merge LWW por campo<br/>(exclusão vence, timestamp futuro limitado)
        API->>API: valida o resultado (regras e limites)
        alt houve mudança efetiva
            API->>PG: grava tarefa (novo changeSeq)
        end
    end
    API->>PG: version+1 uma vez por tarefa alterada no lote<br/>+ evento no outbox + entregas
    API->>PG: COMMIT
    API-->>S: resultado por operação<br/>(recusada → estado do servidor)
    S->>DB: remove ops da fila<br/>(recusada: substitui a cópia local e avisa o usuário)

    S->>API: GET /api/sync/pull?since=cursor (paginado)
    API->>PG: tarefas do usuário com changeSeq > cursor
    API-->>S: tarefas (inclui tombstones e metadados de merge)<br/>+ novo cursor + há mais páginas?
    S->>DB: merge LWW por campo com o estado local<br/>(preserva edições feitas durante o ciclo)
    S->>DB: grava novo cursor (só avança)
    S-->>UI: indicador "Sincronizado"
```

### Estados do indicador de sincronização

```mermaid
stateDiagram-v2
    [*] --> Sincronizado
    Sincronizado --> Pendente: alteração local
    Pendente --> Sincronizando: gatilho de sync (debounce/timer/online)
    Sincronizado --> Sincronizado: pull periódico ou app em foco (o indicador não muda)
    Sincronizando --> Sincronizado: push e pull ok, fila vazia
    Sincronizando --> Pendente: ok, mas novas alterações durante o ciclo
    Sincronizando --> Offline: falha de rede (backoff)
    Offline --> Sincronizando: evento online / nova tentativa
    Sincronizando --> Erro: servidor respondeu erro, 5xx, 4xx ou resposta inválida (backoff)
    Erro --> Sincronizando: nova tentativa
    Sincronizando --> SessaoExpirada: 401
    SessaoExpirada --> Sincronizando: novo login
```

---

## 3. Fluxo da integração externa

Do servidor até os consumidores: assinaturas por usuário, outbox transacional e webhooks com retry. Detalhes e
justificativas na seção 6 do [escopo](escopo.md).

### Contrato de resposta do consumidor

| Resposta | Significado para o Way2Do |
|---|---|
| 2xx | entregue |
| **205 Reset Content** | entregue **e** "mande o retrato completo" (consumidor novo ou que perdeu o estado) |
| 3xx (redirecionamento não é seguido), 4xx, 5xx, timeout, erro de rede | não entregue → nova tentativa com backoff, por até 24 h |

### Cadastro de uma assinatura

```mermaid
sequenceDiagram
    autonumber
    actor U as Usuário (online)
    participant UI as Tela Integrações
    participant API as API
    participant PG as PostgreSQL
    participant C as Consumidor

    Note over C: raiz do consumidor: "gerar nova URL"<br/>entrada /in/T · painel /v/SHA-256(T)
    U->>UI: cola a URL de entrada
    UI->>API: POST /api/integrations { url }
    API->>API: valida formato (http/https)
    API->>API: limite de 10 assinaturas e de tentativas
    API->>API: resolve o host e recusa IP privado,<br/>loopback ou link-local (anti-SSRF)
    API->>C: POST webhook.ping (sem seguir redirecionamento, timeout 5 s)
    alt 200 ou 205 (205 = espaço novo)
        C-->>API: 2xx + cabeçalho X-Way2Do-Dashboard-Url<br/>(o 205 não pode ter corpo)
        API->>API: aceita dashboardUrl só se http/https
        API->>PG: grava assinatura (+ dashboardUrl)<br/>e agenda o retrato completo
        API-->>UI: 201 Created (link "abrir painel")
        Note over PG,C: o worker envia 1 todo.snapshot por tarefa do usuário,<br/>em todo cadastro (o 200 não garante que o consumidor<br/>já tem as tarefas deste usuário)
    else erro, timeout, 3xx, 4xx ou rede interna
        API-->>UI: 400/409/429 com o motivo (assinatura não é gravada)
    end
```

### Entrega das alterações

```mermaid
sequenceDiagram
    autonumber
    participant API as API (push de sync)
    participant PG as PostgreSQL
    participant W as Worker de webhooks
    participant C as Consumidor

    API->>PG: mesma transação: tarefa + outbox_event<br/>+ 1 delivery por assinatura do dono da tarefa
    API-->>W: sinal "há eventos" (após COMMIT)
    Note over W: também acorda por polling<br/>(cobre sinal perdido e reinício)

    W->>PG: SELECT deliveries pendentes, vencidas<br/>ORDER BY changeSeq FOR UPDATE SKIP LOCKED
    W->>W: confere de novo o IP de destino (anti-SSRF)
    W->>C: POST /in/T (timeout 10 s, sem seguir redirecionamento)<br/>X-Way2Do-Event-Id · X-Way2Do-Event-Type

    alt 200
        C->>C: descarta eventId repetido<br/>aplica só se version > atual
        C-->>W: 200 OK
        W->>PG: delivery = delivered
    else 205 (consumidor reiniciou e o espaço está vazio, vale também durante um retrato)
        C-->>W: 205 Reset Content
        W->>PG: delivery = delivered<br/>+ agenda retrato completo (máx. 1 por minuto por assinatura)
    else falha (rede, timeout, 3xx, 4xx, 5xx)
        C--xW: erro
        W->>PG: attempts+1, next_attempt_at = agora + backoff exponencial com jitter
        Note over W,C: nova tentativa mais tarde (at-least-once)
    else passou da janela de 24 h
        W->>PG: delivery = failed (dead-letter, registrado no log)
    end
```

### Garantias em resumo

| Garantia | Como |
|---|---|
| Nada se perde | evento gravado na mesma transação da alteração (outbox) |
| Só após reconciliar | o evento nasce no servidor, no push, e só se o merge mudou algo |
| Só o backend envia | o dispositivo nunca fala com os consumidores |
| At-least-once | reentrega até 2xx, com backoff exponencial por até 24 h, depois dead-letter |
| Idempotência | `eventId` único; o consumidor deduplica |
| Ordem | evento carrega estado completo + `version`; o consumidor ignora versão antiga (vale também para o retrato completo) |
| Recuperação do consumidor | 205 → retrato completo, assíncrono e limitado a 1 por minuto por assinatura |
| Somente leitura | o consumidor só responde às entregas; não altera dados nem chama o Way2Do |
| Falha de um consumidor | entregas independentes por assinatura |
| Mais de uma instância | `FOR UPDATE SKIP LOCKED` evita entrega dupla concorrente |
| Autenticidade | token secreto na URL de entrada; HMAC do corpo fica como evolução |
| SSRF | IPs privados recusados no cadastro e conferidos de novo a cada entrega (liberados só no ambiente local); redirecionamentos não seguidos |
| Abuso do cadastro | até 10 assinaturas por usuário e limite de tentativas |
| XSS no link do painel | `dashboardUrl` aceito só com `http`/`https` |
| XSS no painel do consumidor | conteúdo inserido só via `textContent` + CSP restritiva |
