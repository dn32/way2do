// Falhas das chamadas à API, compartilhadas por login, sincronização e integrações.

/** Sem conexão (ou servidor inalcançável, ou tempo esgotado). */
export class OfflineError extends Error {}

/** 401: o cookie expirou ou não vale mais. Os dados locais ficam; falta só entrar de novo. */
export class SessionExpiredError extends Error {}

/** 409: o cookie é de outro usuário (outra aba trocou de conta). Nada é enviado nem gravado. */
export class OtherUserError extends Error {}

/** Há rede, mas o servidor respondeu com erro (5xx, 429, resposta fora do contrato). */
export class ServerError extends Error {}
