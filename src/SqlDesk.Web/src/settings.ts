/** Preferências simples do editor, guardadas no navegador do WebView (a tela de configurações chega na fase 9). */

const AUTO_ALIAS_KEY = 'autoAlias'

/** Alias automático de tabela no autocomplete. Ligado por padrão. */
export function getAutoAlias(): boolean {
  try {
    return localStorage.getItem(AUTO_ALIAS_KEY) !== '0'
  } catch {
    return true
  }
}

export function setAutoAlias(on: boolean) {
  try {
    localStorage.setItem(AUTO_ALIAS_KEY, on ? '1' : '0')
  } catch {
    /* preferência de conveniência; sem armazenamento, vale só para esta sessão */
  }
}
