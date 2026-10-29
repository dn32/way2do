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
> _Em construção._

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
> _Em construção: endereços da aplicação e do consumidor na VPS (as credenciais vão por e-mail)._

### Testes
> _Em construção._

### Roteiro de demonstração
> _Em construção: regra urgente, offline no DevTools e num celular Android, sincronização, conflito entre aparelhos
> e consumidor recebendo os eventos._

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
