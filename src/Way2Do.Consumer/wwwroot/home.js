// Página inicial: gera um par de URLs (POST /new) e mostra. Texto só via textContent/value.
'use strict';

const generate = document.getElementById('generate');
const result = document.getElementById('result');
const error = document.getElementById('error');
const ingestInput = document.getElementById('ingest-url');
const copyButton = document.getElementById('copy-ingest');
const viewLink = document.getElementById('view-url');

generate.addEventListener('click', async () => {
  generate.disabled = true;
  error.hidden = true;
  try {
    const response = await fetch('/new', { method: 'POST', cache: 'no-store' });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const pair = await response.json();
    ingestInput.value = pair.ingestUrl;
    viewLink.textContent = pair.viewUrl;
    viewLink.href = safeHttpUrl(pair.viewUrl);
    copyButton.textContent = 'Copiar';
    result.hidden = false;
  } catch {
    error.textContent = 'Não foi possível gerar a URL. Tente de novo.';
    error.hidden = false;
  } finally {
    generate.disabled = false;
  }
});

copyButton.addEventListener('click', async () => {
  ingestInput.select();
  try {
    await navigator.clipboard.writeText(ingestInput.value);
    copyButton.textContent = 'Copiada';
  } catch {
    // Sem a API de área de transferência (contexto não seguro): o texto fica selecionado para Ctrl+C.
    copyButton.textContent = 'Use Ctrl+C';
  }
});

// Só aceita http/https no link (o valor vem do servidor, mas um javascript: seria executado ao clicar).
function safeHttpUrl(value) {
  try {
    const url = new URL(value, location.href);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : '/';
  } catch {
    return '/';
  }
}
