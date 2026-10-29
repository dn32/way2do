# Way2Do — instruções para o agente

Desafio técnico: Todo List **offline-first** com sincronização e integração externa via webhooks.
Escopo e decisões: [docs/escopo.md](docs/escopo.md). Diagramas: [docs/arquitetura.md](docs/arquitetura.md).
Etapas, papéis e verificação: [docs/plano-de-execucao.md](docs/plano-de-execucao.md).
Leia o escopo antes de qualquer mudança de comportamento. Ao concluir uma etapa, atualize o placar e o registro de
uso de IA do plano.

## Estrutura

- `src/Way2Do.Api/` — API ASP.NET Core (.NET 10, Minimal APIs, EF Core + Npgsql), organizada por funcionalidade
  (`Auth/`, `Sync/`, `Webhooks/`, `Domain/`).
- `src/Way2Do.Consumer/` — consumidor externo simulado.
- `src/web/` — frontend React + Vite + TypeScript (PWA, Dexie).
- `tests/Way2Do.Api.Tests/` — xUnit (domínio puro + integração com Testcontainers).
- `tests/fixtures/` — casos de teste compartilhados entre C# e TS.
- `docs/` — escopo, arquitetura, plano de execução, marca (`marca.html`) e tokens do design (`design/way2do.css`).
- `.claude/skills/roteiro-demo/` — skill de aceitação ponta a ponta (criada na etapa 9).

## Portas locais

Frontend 5300 · API 5100 (só em desenvolvimento; no Compose a API é interna e responde em `5300/api` pelo nginx) ·
Consumidor 5200 · PostgreSQL 5433. Não usar 5173, 8080 nem 5432 (ocupadas por outros projetos na máquina).

## Regras do projeto

- **A sincronização é nossa.** Não introduzir bibliotecas que resolvam sync offline (Dexie Cloud, RxDB, PouchDB,
  Firebase, Azure Mobile Apps etc.). Dexie é só armazenamento.
- **Um único caminho de escrita**: tarefas só são gravadas no servidor via `POST /api/sync/push`, passando pelo merge.
- **Regra "urgente" e ordenação existem em C# e em TS.** Qualquer mudança vale para os dois lados e para
  `tests/fixtures/urgencia.json`, que as duas suítes consomem. O mesmo vale para a ordenação (`ordenacao.json`) e
  o merge (`merge.json`).
- **Merge LWW por campo** é o mesmo algoritmo no cliente e no servidor; mudar um exige mudar o outro.
- Sem super-engenharia: nada de broker, cache distribuído ou camadas extras sem uma justificativa registrada no escopo.
- **Interface segue a marca**: cores, fontes, raios, ícones e componentes de [docs/marca.html](docs/marca.html), com os
  tokens de [docs/design/way2do.css](docs/design/way2do.css). Nada de cor ou fonte fora dos tokens.
- **Biblioteca nova só com justificativa** na tabela "Bibliotecas e por quê" do escopo (o enunciado exige).
- **Nenhuma credencial no repositório**: senhas e chaves só no `.env` (não versionado); o `.env.example` traz só os
  nomes. As credenciais da demo ficam em `credenciais.local.md` (ignorado).
- **Contrato do webhook** (tipos de evento, 2xx/205, `dashboardUrl`) é público: mudança exige atualizar o escopo.
- Decisão nova ou mudança de decisão → atualizar `docs/escopo.md` (e o README, quando for o caso).
- **COMMIT só depois de revisão**: todo pedido de autorização de commit traz o resultado da revisão do conteúdo
  (critérios na seção 1 do plano de execução). Conteúdo não revisado não é commitado, mesmo com COMMIT.
- Documentação, comentários e mensagens de commit em **português brasileiro**; identificadores de código em inglês.

## Verificação no navegador

- Screenshots em `.playwright-mcp/img/` (ignorado pelo git).
- O navegador de teste (Playwright MCP) bloqueia `file://`: para abrir páginas estáticas (ex.: `docs/marca.html`),
  suba um servidor HTTP temporário em `127.0.0.1` numa porta livre (ex.: `python -m http.server 5310 --bind
  127.0.0.1` na pasta) e encerre-o ao terminar.

## Comandos

Pré-requisito: `.env` na raiz (copiado de `.env.example` e preenchido; não versionado).

```bash
docker compose up -d db                      # Postgres em 127.0.0.1:5433
dotnet build Way2Do.slnx
dotnet test Way2Do.slnx                      # sobe um Postgres próprio em container (Testcontainers)

# API em desenvolvimento (http://localhost:5100). Rodar de dentro da pasta do projeto: o appsettings.Development.json
# é lido do diretório atual.
set -a && . ./.env && set +a && cd src/Way2Do.Api && dotnet run

# Migrations (ferramenta local; restaurar uma vez com `dotnet tool restore`)
dotnet ef migrations add <Nome> --project src/Way2Do.Api --output-dir Data/Migrations
```

Na primeira subida com banco vazio, o EF registra uma falha ao consultar `__EFMigrationsHistory` antes de criá-la; é
esperado e não se repete.
