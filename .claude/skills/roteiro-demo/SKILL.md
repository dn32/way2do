---
name: roteiro-demo
description: Executa o roteiro da demonstração do Way2Do como teste de aceitação ponta a ponta — navegador (Playwright MCP, três perfis) e celular Android real (MCP mobile + adb), no ambiente local (Docker Compose) ou na VPS. Use para conferir a entrega, depois de correções relevantes e como ensaio antes da apresentação. Argumento: local | vps | celular | tudo (padrão: tudo).
---

# Roteiro da demonstração (aceitação ponta a ponta)

Percorre o roteiro do [escopo](../../../docs/escopo.md) (seção 14, "Roteiro da demonstração") conferindo cada
resultado esperado, tira screenshots e devolve o placar. Cada passo tem um **esperado** objetivo: se não bater, o
passo é ❌ com o motivo, e o roteiro continua no que for independente.

## Antes de começar

- **Ambiente**:
  - `local`: `docker compose up -d --build` na raiz e esperar os quatro serviços `healthy` (`docker compose ps`).
    App em `http://localhost:5300`, consumidor em `http://localhost:5200`. A URL de entrada gerada pelo consumidor
    é a interna (`http://consumer:8080/in/…`).
  - `vps`: app em `https://way2do.startbit.com.br`, consumidor em `https://way2do-consumer.startbit.com.br`
    (conferir `curl -s -o /dev/null -w "%{http_code}"` = 200 nos dois). A URL de entrada é a pública.
  - `celular`: sempre contra a VPS (HTTPS é exigido para o Service Worker fora de `localhost`).
- **Credenciais**: leia `credenciais.local.md` (não versionado). As senhas entram só no código executado; **nunca**
  em texto de resposta, screenshot de campo preenchido ou arquivo versionado.
- **Perfis do navegador** (cada um é um "aparelho" com IndexedDB próprio):
  - `pw-perfil1` = **aparelho A** (ana), desktop 1280×860;
  - `pw-perfil2` = **aparelho B** (ana), celular 390×844;
  - `pw-perfil3` = **painel do consumidor** e o teste de isolamento com **bruno**.
  Antes do primeiro passo, em cada perfil: `context().setOffline(false)`, navegar até a base, aceitar "Nova versão
  disponível" (botão "Atualizar") se aparecer e **esperar o primeiro ciclo** terminar em "Sincronizado" ou em
  "Sessão expirada" (`.sync--ok, .sync--expired`) — uma sessão antiga só é detectada nessa primeira tentativa. Se
  expirou, "Entrar de novo", **esperar o formulário de login aparecer** e logar.
- **Dados**: toda tarefa criada pelo roteiro começa com `Roteiro ·` (no celular, `Roteiro -`: o `adb` não digita
  "·"). O passo 0 apaga sobras de execuções anteriores (`/^Roteiro( ·| -) /`); o passo final apaga o que o roteiro
  criou (a menos que o pedido seja manter para a apresentação). Tarefas que não começam assim não são do roteiro e
  ficam intactas.
- **Screenshots**: `.playwright-mcp/img/roteiro-demo/<ambiente>/NN-nome.png` (pasta ignorada pelo git).
- **Placar**: republique a lista inteira a cada passo (✅ ⏳ ⬜, ❌ com motivo), como manda o CLAUDE.md.

## Trechos reutilizáveis (Playwright, dentro de `browser_run_code_unsafe`)

```js
// criar tarefa (funciona no desktop e no celular)
const criar = async (page, { titulo, descricao, prioridade, tags = [] }) => {
  await page.getByRole('button', { name: 'Nova tarefa', exact: true }).first().click();
  await page.getByLabel('Título').fill(titulo);
  if (descricao) await page.getByLabel('Descrição').fill(descricao);
  if (prioridade) await page.getByRole('group', { name: 'Prioridade' }).getByRole('button', { name: prioridade }).click();
  for (const t of tags) { await page.locator('#task-tag').fill(t); await page.keyboard.press('Enter'); }
  await page.getByRole('button', { name: 'Salvar tarefa' }).click();
};
const abrir = (page, titulo) => page.getByRole('button', { name: titulo, exact: true }).click(); // título da tarefa
const concluir = (page, titulo) => page.getByRole('button', { name: `Concluir: ${titulo}` }).click();
const barra = (page) => page.locator('.sync').first().innerText(); // indicador de sincronização
const sincronizado = (page) => page.locator('.sync--ok').first().waitFor({ timeout: 40000 });
const lista = (page) => page.locator('main').innerText();
```

No celular (390 px), filtros e tags ficam no painel "Filtros"/"Tags" da barra inferior; Integrações e Sair, no
menu (`Menu da conta`).

## Passos

0. **Preparação** — logar ana em A e B, bruno ainda não. Excluir tarefas `Roteiro ·` que sobraram (abrir, "Excluir
   tarefa", confirmar) e remover integrações de execuções anteriores. Esperado: A e B "Sincronizado".
1. **Login e CRUD (A)** — criar `Roteiro · Comprar cabo 10mm` (Alta, tags `obra`, `elétrica`), `Roteiro · Medir
   quadro` (tag `obra`) e `Roteiro · Apagar depois`; editar a descrição da primeira; excluir a terceira.
   Esperado: duas tarefas `Roteiro ·` na lista; a primeira com selo de prioridade alta e as duas tags.
2. **Filtros e busca (A)** — filtrar pela tag `obra` (duas aparecem), status "Concluídas" (nenhuma `Roteiro ·`),
   buscar `eletrico` sem acento (acha `Roteiro · Comprar cabo 10mm` pela descrição "quadro elétrico"; a busca cobre
   título e descrição, não tags), limpar. Screenshot.
3. **Regra urgente (A)** — criar `Roteiro · Urgente: trocar disjuntor`: vai para o topo dos pendentes com o selo
   URGENTE. Editar o título para `Roteiro · Trocar disjuntor, não é urgente`: perde o selo e sai do topo.
   Voltar para `Roteiro · Urgente: trocar disjuntor` e concluir: vai para "Concluídas" (urgente concluída fica no
   fim). Screenshot em cada um dos três estados.
4. **Offline (A)** — `setOffline(true)`. Esperado: barra "Offline". Criar `Roteiro · Feita offline`, editar a
   descrição de `Roteiro · Medir quadro`, concluir `Roteiro · Comprar cabo 10mm`, excluir uma tarefa criada
   offline. Esperado: barra "Offline · N pendentes" (N ≥ 3). **Recarregar a página ainda offline**: o app abre
   pelo Service Worker, com a lista e as pendências. Screenshot.
5. **Volta online (A → B)** — `setOffline(false)`. Esperado em A: "Sincronizando…" e depois "Sincronizado", fila
   zerada. Em B: as alterações chegam sozinhas em até ~30 s (pull periódico) ou ao voltar o foco. Screenshot de B.
6. **Conflito por campo (A × B)** — os dois offline; A muda o título de `Roteiro · Medir quadro` para
   `Roteiro · Medir quadro do 2º andar`; B conclui `Roteiro · Medir quadro`. Os dois online (A primeiro, depois B,
   depois recarregar A). Esperado nos dois: **concluída e com o título novo**. Screenshot dos dois.
7. **Isolamento (bruno)** — em `pw-perfil3`, logar bruno. Esperado: nenhuma tarefa `Roteiro ·`. Sair.
8. **Integração** — `pw-perfil3`: abrir o consumidor, "Gerar nova URL", copiar a URL de entrada. A: Integrações →
   colar → "Testar e salvar". Esperado: "Integração cadastrada", URL mascarada (`…/…xxxx`), link "Abrir painel".
   Abrir o painel em `pw-perfil3`: as tarefas de ana já estão lá (retrato completo do cadastro).
   Em A, criar `Roteiro · Ao vivo`. Esperado: aparece no painel em poucos segundos (o app espera ~2 s antes de
   enviar). Screenshot do painel.
9. **Modo de falha** — no painel, "Instável"; em A, criar `Roteiro · Instável 1..3`. Esperado: as três aparecem
   uma única vez e "Duplicados descartados" > 0 (as reentregas). "Fora do ar"; em A, criar `Roteiro · Durante a
   queda`: não chega, e "Recusadas de propósito" cresce. "Desligado": chega em alguns segundos (backoff).
   Screenshot.
10. **Reinício do consumidor e 205** — `local`: `docker compose restart consumer`; `vps`:
    `ssh vps 'cd /opt/way2do && sudo docker compose -f docker-compose.yml -f docker-compose.vps.yml restart consumer'`.
    Esperado: `/v/<H>/state` responde 404 (memória vazia). Em A, concluir ou editar uma tarefa: o consumidor
    responde 205 e o retrato completo repõe tudo ("Retratos pedidos (205)" = 1). Screenshot.
11. **Celular (Android, contra a VPS)** — ver a seção abaixo.
12. **Limpeza** — excluir as tarefas `Roteiro ·` (em A) e remover a integração; conferir em B que sumiram.

## Celular (MCP mobile + adb)

O aparelho precisa aparecer em `mobile_list_available_devices` (USB ou depuração por Wi-Fi pareada) e estar
**desbloqueado** (peça ao dono; não há como passar pelo PIN). Durante o roteiro, `adb shell svc power stayon usb`
mantém a tela ligada; no fim, `adb shell svc power stayon false` devolve a configuração.

Cuidados que vieram da primeira execução:
- abra a URL por intent, numa aba própria (o `mobile_open_url` às vezes só traz a aba que já estava aberta):
  `adb shell am start -a android.intent.action.VIEW -d https://way2do.startbit.com.br -e com.android.browser.application_id way2do-roteiro com.android.chrome`;
- **sempre** `mobile_list_elements_on_screen` antes de tocar e toque pela referência (`@eN`): os campos do app
  aparecem pelo id (`login-user`, `login-password`, `task-title`); toque por coordenada numa tela que ainda está
  carregando pode cair em outro lugar (na primeira execução caiu num atalho da página inicial do Chrome);
- `mobile_type_keys` usa o `adb input`, que só digita ASCII: nada de "·" ou acentos no que for digitado;
- feche o teclado com `adb shell input keyevent KEYCODE_BACK` antes de procurar botões no fim da tela.

1. Abrir o app (intent acima) e logar ana (`@login-user`, `@login-password`, enviar). Esperado: barra
   "Sincronizado".
2. **Instalar a PWA**: menu ⋮ do Chrome → "Instalar e criar atalho" → "Instalar" → confirmar "Instalar". O
   pacote é um WebAPK (`adb shell pm list packages | grep webapk`; o do Way2Do é o de `firstInstallTime` mais
   recente em `adb shell dumpsys package <pacote>`). Abrir com `mobile_launch_app` nesse pacote: o app abre em tela
   própria, sem a barra do Chrome.
3. **Modo avião**: `adb shell cmd connectivity airplane-mode enable` (conferir com
   `adb shell settings get global airplane_mode_on` = 1). Esperado: barra "Offline".
4. **Fechar e reabrir sem rede**: `mobile_terminate_app` na PWA **e** no Chrome (`com.android.chrome`; conferir
   com `adb shell pidof com.android.chrome` vazio) e abrir de novo pelo pacote da PWA. Esperado: o app abre, com a
   lista e a barra "Offline". Screenshot — **é a prova principal**.
5. Offline no celular: criar `Roteiro - Celular urgente sem sinal` (Alta, tag `obra`; sobe com o selo URGENTE) e
   concluir uma tarefa. Esperado: "Offline · 2 pendentes". Conferir no painel (`/v/<H>/state`) que **ainda não**
   chegou nada: as alterações existem só no aparelho.
6. **Desligar o modo avião**: `adb shell cmd connectivity airplane-mode disable`. Esperado: "Sincronizado" no
   celular; no navegador desktop (A, VPS) e no painel do consumidor, as alterações do celular aparecem.
   Screenshots do celular e do painel.

Se algum passo do celular falhar por limitação do aparelho (ex.: o comando de modo avião exigir permissão), use o
modo avião pela interface (`mobile_swipe_on_screen` para abrir as configurações rápidas) e registre como foi.

## Resultado

Responda com o placar final (todos os passos, ✅/❌ com motivo), a lista das screenshots e o que precisou de
correção. Se algo falhou por defeito do app: corrija (com teste), rode de novo o passo e registre no plano de
execução.
