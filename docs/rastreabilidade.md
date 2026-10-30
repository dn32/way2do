# Rastreabilidade — enunciado → solução → verificação

Cada item do enunciado do desafio, onde ele é atendido e como foi verificado. Serve de roteiro para conferir a
entrega sem precisar procurar no código.

**Legenda da situação**
- **Atendido**: implementado como pedido.
- **Interpretação**: atendido segundo uma leitura registrada nas [premissas do README](../README.md#3-premissas),
  onde o enunciado deixou espaço ou onde a leitura literal daria um resultado pior para o usuário.
- **Na entrega**: ação de envio, conferida no momento da entrega (não depende do código).

**Legenda da verificação**
- **API**, **Consumidor**, **Web**: suítes automatizadas (`dotnet test Way2Do.slnx` e `npm test` em `src/web`); o
  nome citado é o do teste.
- **Casos compartilhados**: os arquivos de [`tests/fixtures/`](../tests/fixtures), executados pelas suítes C# e TS
  ([`SharedFixtureTests.cs`](../tests/Way2Do.Api.Tests/Domain/SharedFixtureTests.cs) e
  [`fixtures.test.ts`](../src/web/src/domain/fixtures.test.ts)).
- **Roteiro n**: passo n do roteiro de aceitação ([skill `roteiro-demo`](../.claude/skills/roteiro-demo/SKILL.md)),
  executado no navegador (local e instância de demonstração); **Celular n**: passo n da parte do celular, num
  Android real contra a instância de demonstração.

---

## 1. Cenário e requisitos de negócio

| Item do enunciado | Onde está atendido | Como foi verificado | Situação |
|---|---|---|---|
| Web para desktop e celular | layouts próprios para cada um ([`TasksScreen.tsx`](../src/web/src/ui/TasksScreen.tsx), `useIsDesktop`); PWA instalável ([`vite.config.ts`](../src/web/vite.config.ts)) | Roteiro (perfil desktop e perfil celular); Celular 1–2 (instalação da PWA) | Atendido |
| Totalmente funcional offline no celular: criar, editar, concluir e remover sem conexão | a tela lê e grava só no IndexedDB ([`localTasks.ts`](../src/web/src/data/localTasks.ts)); o Service Worker faz o app **abrir** sem rede | Web: `cria a tarefa e enfileira a operação na mesma transação`, `excluir marca a tarefa (tombstone)…`; Roteiro 4 (inclusive recarregar a página offline); Celular 3–5 (app fechado e reaberto em modo avião) | Atendido |
| Mudanças sincronizam automaticamente quando a conexão volta | motor em [`engine.ts`](../src/web/src/sync/engine.ts): volta da rede, abertura, 2 s após cada alteração, volta ao primeiro plano, a cada 30 s | Web: `edições offline sobem quando a conexão volta, e a fila esvazia`, `alteração local é enviada depois do debounce…`; Roteiro 5; Celular 6 | Atendido |
| ~1000 usuários simultâneos | dimensionamento justificado no [README](../README.md#dimensionamento-para-1000-usuários-simultâneos) e no [escopo, seção 9](escopo.md#9-dimensionamento-para-1000-usuários-simultâneos) | justificativa (o enunciado dispensa teste de carga); índices e lock por usuário cobertos por API: `Push_espera_o_lock_do_usuario`, `Pulls_simultaneos_a_pushes_paralelos_nao_perdem_alteracoes` | Atendido |

## 2. Requisitos funcionais

| # | Item do enunciado | Onde está atendido | Como foi verificado | Situação |
|---|---|---|---|---|
| 1 | Autenticação; cada usuário acessa só as próprias tarefas | JWT em cookie `HttpOnly` ([`Auth/`](../src/Way2Do.Api/Auth)); toda consulta filtra pelo dono ([`SyncService.cs`](../src/Way2Do.Api/Sync/SyncService.cs)); um banco local por usuário ([`db.ts`](../src/web/src/data/db.ts)) | API: `Pull_so_traz_tarefas_do_proprio_usuario`, `Operacao_sobre_tarefa_de_outro_usuario_e_recusada_sem_revelar_a_tarefa`, `Cada_usuario_ve_e_remove_so_as_proprias_integracoes`, `Cookie_adulterado_e_recusado`; Web: `não sincroniza o banco de um usuário que não é o da sessão`; Roteiro 7 (bruno não vê nada da ana) | Atendido |
| 2 | CRUD com título (obrigatório), descrição, vencimento, prioridade (baixa/média/alta) e status (pendente/concluída) | modelo em [`types.ts`](../src/web/src/domain/types.ts) e [`Entities.cs`](../src/Way2Do.Api/Data/Entities.cs); formulário em [`TaskForm.tsx`](../src/web/src/ui/TaskForm.tsx); validação igual nos dois lados ([`merge.ts`](../src/web/src/domain/merge.ts), [`TodoMerge.cs`](../src/Way2Do.Api/Domain/TodoMerge.cs)) | API: `Titulo_em_branco_e_recusado`, `Valor_invalido_e_recusado`, `Limites_de_tamanho`; Web: `edição inválida não grava a tarefa nem a operação`; Roteiro 1 | Atendido |
| 3 | Agrupamento (listas, categorias ou tags) | **tags**, com contagem e filtro ([`filters.ts`](../src/web/src/ui/filters.ts), [`TaskList.tsx`](../src/web/src/ui/TaskList.tsx)); identidade da tag igual nos dois lados | Casos compartilhados (`tags.json`); Web: `conta filtros ativos e tags em uso`; Roteiro 1–2 | Atendido (escolha justificada nas premissas) |
| 4 | Concluir e reabrir | botão na própria tarefa ([`TaskItem.tsx`](../src/web/src/ui/TaskItem.tsx)) e campo status no formulário | Roteiro 3 (concluir e reabrir) e 4; Celular 5 | Atendido |
| 5 | Filtrar e/ou buscar (status, prioridade, texto) | status, prioridade, tag e busca sem maiúsculas nem acentos no título e na descrição ([`filters.ts`](../src/web/src/ui/filters.ts)) | Web: `busca ignora maiúsculas e acentos, no título e na descrição`, `filtra por status, prioridade e tag visível`; Roteiro 2 | Atendido |
| 6 | Urgentes no topo: "urgente" no título ou na descrição, sem diferenciar maiúsculas | regra em [`urgency.ts`](../src/web/src/domain/urgency.ts) e [`UrgencyRule.cs`](../src/Way2Do.Api/Domain/UrgencyRule.cs); ordenação em [`ordering.ts`](../src/web/src/domain/ordering.ts) e [`TodoOrdering.cs`](../src/Way2Do.Api/Domain/TodoOrdering.cs) | Casos compartilhados (`urgencia.json`, `ordenacao.json`); Roteiro 3 | Interpretação: variações da palavra contam, menção negada ("não é urgente") não sobe e urgente **concluída** vai para o fim |
| 6 | Critério de ordenação entre urgentes, definido e justificado | pendentes → urgentes → vencimento mais próximo → prioridade → criação → id ([README](../README.md#regra-urgente-e-ordenação)) | Casos compartilhados (`ordenacao.json`) | Atendido |
| 6 | A regra vale offline: tarefa criada **ou editada** sem conexão que passe a conter "urgente" sobe na hora | urgência calculada do texto no aparelho, não gravada; a lista se reordena a cada gravação local (`useLiveQuery`) | Roteiro 4 (tarefa editada offline passa a ser urgente e sobe com o selo); Celular 5 (tarefa urgente criada em modo avião) | Atendido |
| 7 | Leitura e escrita sem conexão | ver seção 1 | ver seção 1 | Atendido |
| 7 | Persistência local e sincronização automática | fila de operações gravada na mesma transação da tarefa; push, depois pull com merge ([`engine.ts`](../src/web/src/sync/engine.ts), [`store.ts`](../src/web/src/sync/store.ts)) | Web: os testes do `describe('sincronização')`; API: `Push_cria_a_tarefa_e_o_pull_devolve_com_os_metadados_do_merge`, `Reenviar_a_mesma_operacao_nao_gera_nova_versao_nem_novo_evento` | Atendido |
| 7 | Indicador claro do estado de sincronização | Offline · Pendente (N) · Sincronizando · Sincronizado · Erro · Sessão expirada ([`indicator.ts`](../src/web/src/sync/indicator.ts), [`SyncBar.tsx`](../src/web/src/ui/SyncBar.tsx)) | Web: `describe('indicador')`; Roteiro 4–5; Celular 3 | Atendido |
| — | Conflitos (pedido no README: "mesma tarefa alterada em mais de um lugar") | LWW por campo, tags uma a uma, exclusão vence edição; mesmo algoritmo em C# e TS | Casos compartilhados (`merge.json`); Web: `conflito entre dois aparelhos é resolvido por campo: os dois convergem`, `edição feita durante o ciclo não é sobrescrita pelo pull…`; Roteiro 6 | Atendido |
| 8 | Push dos Todo Items para uma ou mais aplicações externas ao criar, alterar e remover | assinaturas por usuário (até 10) ([`IntegrationEndpoints.cs`](../src/Way2Do.Api/Webhooks/IntegrationEndpoints.cs)); eventos `todo.created`, `todo.updated`, `todo.deleted` ([`OutboxWriter.cs`](../src/Way2Do.Api/Webhooks/OutboxWriter.cs)) | API: `Tipo_do_evento_por_mudanca`, `Cada_alteracao_de_conteudo_gera_versao_evento_e_entrega_por_assinatura`, `Alteracao_e_entregue_com_o_estado_completo_e_os_cabecalhos`; Roteiro 8 | Atendido (integração por usuário: premissa) |
| 8 | Quase tempo real, só com conexão | o worker é acordado logo após o commit e também roda por polling ([`WebhookDispatcher.cs`](../src/Way2Do.Api/Webhooks/WebhookDispatcher.cs)); consumidor fora do ar gera novas tentativas | API: `Push_acorda_o_worker`, `Falha_reagenda_com_backoff_e_as_demais_da_assinatura_esperam_junto`; Roteiro 8–9 | Atendido |
| 8 | Somente leitura para os consumidores | o consumidor só responde às entregas; não há rota de escrita para ele | leitura do contrato ([escopo, seção 6](escopo.md#6-integração-externa--outbox-transacional--webhooks)); o 205 é resposta à entrega (premissa) | Atendido |
| 8 | Alteração offline só propaga depois de reconciliada no servidor, nunca do aparelho | o evento é gravado no outbox na mesma transação do merge ([`SyncService.cs`](../src/Way2Do.Api/Sync/SyncService.cs)); o frontend só fala com a própria API (CSP `default-src 'self'` em [`security-headers.conf`](../src/web/nginx/security-headers.conf)) | API: `Tarefa_criada_e_excluida_no_mesmo_lote_nao_gera_evento`, `Varias_operacoes_na_mesma_tarefa_no_lote_geram_uma_versao_e_um_evento`; Celular 5–6 (nada chega ao painel antes de a rede voltar) | Atendido |
| 8 | Técnica e garantias justificadas: at-least-once, ordem, idempotência, retry com backoff, falhas do consumidor | outbox transacional + webhooks; reentrega por 24 h com backoff e jitter; `eventId` para idempotência; `version` contra evento atrasado; 205 para consumidor que perdeu o estado | API: `Depois_de_24_horas_a_entrega_desiste`, `Duas_instancias_ao_mesmo_tempo_nao_entregam_a_mesma_alteracao_duas_vezes`, `Conexao_que_cai_no_meio_da_resposta_e_falha_com_nova_tentativa`, `Consumidor_lento_nao_segura_a_entrega_dos_outros`, `Ping_205_envia_o_retrato_so_das_tarefas_do_dono_e_nao_excluidas`; Consumidor: `EventId_repetido_conta_como_duplicado_e_nao_reaplica`, `Versao_antiga_nao_sobrescreve_a_nova_e_conta_como_desatualizada`; Roteiro 9–10 | Atendido |
| 8 | Consumidor externo simulado | [`src/Way2Do.Consumer`](../src/Way2Do.Consumer): registra o que recebeu, mostra as tarefas e a saúde da integração, tem modo de falha | suíte Consumidor (80 testes); Roteiro 8–10 | Atendido |

## 3. Restrições técnicas

| Item | Onde está atendido | Situação |
|---|---|---|
| Backend obrigatório em .NET | ASP.NET Core .NET 10 ([`Way2Do.Api.csproj`](../src/Way2Do.Api/Way2Do.Api.csproj)) | Atendido |
| Frontend livre, Angular desejável; justificar outra stack | React + Vite + TypeScript, justificado no [README](../README.md#stack-e-banco-de-dados) | Atendido |
| Infra livre, escolhida e justificada | PostgreSQL (motivos e alternativa descartada no README), Docker Compose, instância de demonstração com Cloudflare Tunnel ([README](../README.md#implantação)) | Atendido |
| Bibliotecas justificadas; nada pronto resolvendo a sincronização | tabela "Bibliotecas e por quê" do [README](../README.md#bibliotecas-e-por-quê), conferida contra o `package.json` e os `.csproj`; Dexie só como armazenamento, sincronização escrita do zero | Atendido |

## 4. Fora de escopo

Nada exigido ficou de fora por engano: os itens que o enunciado dispensa e o que foi feito com cada um.

| Item dispensado | Na solução |
|---|---|
| Cadastro e recuperação de senha | não feitos; usuários pré-cadastrados com senha definida no `.env` (limitação registrada) |
| Design visual sofisticado | não era exigido; a interface segue a marca do projeto ([`docs/marca.html`](marca.html)) |
| Idiomas, temas, notificações push, compartilhamento entre usuários | não feitos |
| Teste de carga | não feito; dimensionamento justificado |
| Publicação na nuvem | não exigida; o projeto roda localmente pelo README. A instância de demonstração existe para provar o offline num celular real (o Service Worker exige HTTPS fora de `localhost`) |

## 5. Entregáveis

| Item | Onde está atendido | Como foi verificado | Situação |
|---|---|---|---|
| Código-fonte (backend, frontend, consumidor) em repositório Git, com commits ao longo do desenvolvimento | este repositório; um commit por etapa do [plano](plano-de-execucao.md#3-etapas) | histórico do git | Atendido |
| Diagrama da visão geral | [arquitetura, seção 1](arquitetura.md#1-visão-geral) | os diagramas Mermaid passaram pelo parser oficial | Atendido |
| Diagrama da sincronização offline, da gravação local à reconciliação | [arquitetura, seção 2](arquitetura.md#2-fluxo-da-sincronização-offline), com os estados do indicador | idem | Atendido |
| Diagrama da integração externa, do servidor aos consumidores | [arquitetura, seção 3](arquitetura.md#3-fluxo-da-integração-externa) (cadastro e entrega) | idem | Atendido |
| README: como executar (pré-requisitos, passo a passo da aplicação e do consumidor, usuários de teste) | [README, seção 1](../README.md#1-como-executar) | clone do GitHub numa pasta limpa seguindo só o README | Atendido |
| README: decisões e trade-offs com os seis temas (stack e banco, sincronização, conflitos, integração e garantias, ordenação das urgentes, dimensionamento) | [README, seção 2](../README.md#2-decisões-técnicas-e-trade-offs), um subtítulo por tema | leitura | Atendido |
| README: premissas | [README, seção 3](../README.md#3-premissas); cada premissa conferida na seção 7 abaixo | leitura e código | Atendido |
| README: limitações e próximos passos, com o "como faria" | [README, seção 4](../README.md#4-limitações-e-próximos-passos) | leitura | Atendido |
| Testes automatizados, sobretudo da sincronização e da regra urgente | 237 testes na API, 80 no consumidor, 172 no frontend; as regras dos dois lados rodam contra os mesmos casos | as três suítes verdes | Atendido |
| Uso de IA contado no README | [README, uso de IA](../README.md#uso-de-inteligência-artificial) e registro por etapa no [plano](plano-de-execucao.md#5-registro-de-uso-de-ia) | leitura | Atendido |

## 6. Critérios de avaliação e checklist "Antes de enviar"

| Critério | Onde observar |
|---|---|
| Arquitetura e dimensionamento | [arquitetura.md](arquitetura.md); README seção 2 (dimensionamento e "por que não" de cada alternativa descartada) |
| Offline e sincronização | seções 1 e 2 acima (itens 7 e conflitos); Roteiro 4–6; Celular |
| Regras de negócio | seção 2 acima (itens 2 a 6); Roteiro 1–3 |
| Integração externa | seção 2 acima (item 8); README "Integração externa e garantias de entrega" |
| Qualidade do código | organização por funcionalidade (API) e por camada (frontend: `domain`, `data`, `sync`, `api`, `ui`); regras críticas em funções puras com testes em tabela; revisão pelos princípios SOLID registrada no plano (etapa 11) |
| Comunicação | README; limitações declaradas sem omitir as que afetam segurança e consistência |

| Checklist do enunciado | Situação |
|---|---|
| Código funcionando e no repositório | Atendido; conferido de novo no ensaio final da entrega |
| README com as quatro partes | Atendido |
| Desenho da arquitetura no repositório | Atendido |
| Consumidor simulado incluído e com instruções | Atendido (sobe junto no `docker compose up`; README seção 1) |
| Acesso liberado aos avaliadores, se o repositório for privado | Na entrega |
| Resposta ao e-mail com "responder a todos" e o link | Na entrega |
| Envio com no mínimo 2 horas de antecedência | Na entrega |

## 7. Premissas: o comportamento implementado é o descrito

| Premissa (README) | Onde está no código | Como foi verificado |
|---|---|---|
| Login exige conexão; depois o app funciona offline sem prazo | a sessão fica no IndexedDB ([`session.ts`](../src/web/src/data/session.ts)); a tela não depende do token | Roteiro 4; Celular 3–4 (reabrir sem rede) |
| Token expirado offline: o app segue funcionando, a sincronização pausa e pede novo login, sem perder nada | o ciclo devolve `expired` e o motor pausa ([`engine.ts`](../src/web/src/sync/engine.ts)); `expireSession` só esquece a sessão, sem tocar no banco nem na fila ([`session.ts`](../src/web/src/data/session.ts)) | Web: `login novo apaga o "Sessão expirada" gravado no banco do usuário`; no navegador, com a sessão expirada e sem rede: criar, editar, concluir, reabrir e excluir funcionaram, com a barra "Sessão expirada · Entrar de novo" à vista |
| Relógios razoavelmente corretos | LWW usa o horário do aparelho; horário no futuro é limitado ao do servidor | API: `Timestamp_no_futuro_e_limitado_ao_horario_do_servidor`; Web: `timestamps ficam em milissegundos e limitados ao relógio de quem aplica` |
| "Contém urgente" inclui variações e exclui menções negadas | [`urgency.ts`](../src/web/src/domain/urgency.ts), [`UrgencyRule.cs`](../src/Way2Do.Api/Domain/UrgencyRule.cs) | Casos compartilhados (`urgencia.json`); Roteiro 3 |
| Concluída vai para o fim mesmo se urgente; entre as concluídas, urgentes primeiro | [`ordering.ts`](../src/web/src/domain/ordering.ts), [`TodoOrdering.cs`](../src/Way2Do.Api/Domain/TodoOrdering.cs) | Casos compartilhados (`ordenacao.json`); Roteiro 3 |
| Agrupamento por tags | ver requisito 3 | ver requisito 3 |
| Integração por usuário; cadastrar e remover exige conexão | assinatura com dono ([`IntegrationEndpoints.cs`](../src/Way2Do.Api/Webhooks/IntegrationEndpoints.cs)); sem rede, a tela de integrações avisa e não grava ([`IntegrationsPanel.tsx`](../src/web/src/ui/IntegrationsPanel.tsx)) | API: `Cada_usuario_ve_e_remove_so_as_proprias_integracoes`; leitura do código (sem rede, o painel mostra "Integrações precisam de internet" e desabilita as ações) |
| "Somente com conexão" vale para servidor → consumidor: consumidor fora do ar gera novas tentativas | backoff no [`WebhookDispatcher.cs`](../src/Way2Do.Api/Webhooks/WebhookDispatcher.cs) | API: `Falha_reagenda_com_backoff_e_as_demais_da_assinatura_esperam_junto`; Roteiro 9 (fora do ar e recuperação) |
| O 205 não é escrita na aplicação | o consumidor só responde à entrega ([`IngestEndpoints.cs`](../src/Way2Do.Consumer/Spaces/IngestEndpoints.cs)) | Consumidor: `Primeiro_evento_num_espaco_vazio_pede_o_retrato_com_205_e_os_seguintes_recebem_200`; Roteiro 10 |
| Exclusões propagadas como `todo.deleted`, com o último estado | [`OutboxWriter.cs`](../src/Way2Do.Api/Webhooks/OutboxWriter.cs) | API: `Tipo_do_evento_por_mudanca`; Consumidor: `Exclusao_marca_a_tarefa_e_tira_dos_contadores_ativos` |
| Logout não apaga o banco local; as pendências vão no próximo login do mesmo usuário; outro usuário usa o próprio banco | [`session.ts`](../src/web/src/data/session.ts) (`endSession` não apaga o banco); bancos `way2do-<id>` | Web: `describe('sessão e logout')`, `não sincroniza o banco de um usuário que não é o da sessão`; leitura do código (`userDb(id)` abre um banco por usuário) |

## 8. Lacunas encontradas nesta revisão

| Lacuna | Tratamento |
|---|---|
| O roteiro do navegador não provava a regra urgente **offline**: só o passo do celular criava uma tarefa urgente sem rede, e nenhum passo editava uma tarefa offline até ela virar urgente | Roteiro 4 passou a editar uma tarefa sem conexão até ela conter "urgente" e conferir que ela sobe com o selo na hora; executado no navegador sem rede: subiu para o topo dos pendentes com o selo URGENTE |
| Nenhum passo do roteiro reabria uma tarefa concluída | Roteiro 3 passou a reabrir a tarefa urgente concluída; executado no navegador: concluída, foi para "Concluídas"; reaberta, voltou ao topo com o selo |
| O README não dizia que, depois do logout, o banco local continua legível por quem tem acesso ao aparelho (o escopo dizia) | registrado nas limitações do README, com o próximo passo |
