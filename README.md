# Way2Do — Todo List offline-first

Lista de tarefas web (desktop e celular) que funciona **totalmente offline**: as alterações ficam no aparelho e
sincronizam sozinhas quando a conexão volta. O servidor reconcilia as alterações e as propaga, em quase tempo
real, para aplicações externas via webhooks.

- **Backend**: ASP.NET Core (.NET 10) + PostgreSQL
- **Frontend**: React + Vite + TypeScript (PWA, IndexedDB)
- **Integração**: outbox transacional + webhooks com retry
- **Consumidor externo simulado**: ASP.NET Core

Arquitetura e diagramas: [docs/arquitetura.md](docs/arquitetura.md) · Escopo detalhado: [docs/escopo.md](docs/escopo.md) · Plano de execução: [docs/plano-de-execucao.md](docs/plano-de-execucao.md) · Marca: [docs/marca.html](docs/marca.html) (abrir localmente no navegador)

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
4. Suba tudo: `docker compose up --build`. Na primeira vez o build leva alguns minutos; a aplicação está pronta
   quando os quatro serviços aparecem como `healthy` em `docker compose ps`.
5. Abra http://localhost:5300 e entre com `ana` ou `bruno`.

Para ver a integração externa: abra http://localhost:5200, clique em **Gerar nova URL**, copie a **URL de entrada**
(`http://consumer:8080/in/…`, o endereço que a API enxerga dentro do Docker), cadastre-a na tela **Integrações** do
Way2Do e use o link **Abrir painel**.

As portas 5300, 5200 e 5433 precisam estar livres. `docker compose down` para tudo e mantém os dados (volume do
Postgres); `docker compose down -v` apaga também os dados.

### Usuários de teste
Os usuários `ana` e `bruno` são criados automaticamente na primeira subida, com as senhas definidas no `.env`.
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

> _Em construção. Resumo do que será detalhado aqui (versão completa em [docs/escopo.md](docs/escopo.md))._

- **Stack e banco de dados**
- **Mecanismo de sincronização**
- **Tratamento de conflitos**
- **Integração externa e garantias de entrega**
- **Regra "urgente" e critério de ordenação**
- **Dimensionamento para 1000 usuários simultâneos**

---

## 3. Premissas

> _Em construção._

---

## 4. Limitações e próximos passos

> _Em construção._

---

## Bibliotecas e por quê

> _Em construção (tabela do [escopo](docs/escopo.md), seção 2)._

---

## Uso de inteligência artificial

> _Em construção: como as ferramentas de IA foram usadas no desenvolvimento._
