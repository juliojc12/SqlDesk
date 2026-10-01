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

const CSV_DELIMITER_KEY = 'csvDelimiter'

export type CsvDelimiter = ';' | ','

/** Separador do CSV exportado: ponto e vírgula por padrão (o Excel em português espera isso). */
export function getCsvDelimiter(): CsvDelimiter {
  try {
    return localStorage.getItem(CSV_DELIMITER_KEY) === ',' ? ',' : ';'
  } catch {
    return ';'
  }
}

export function setCsvDelimiter(d: CsvDelimiter) {
  try {
    localStorage.setItem(CSV_DELIMITER_KEY, d)
  } catch {
    /* preferência de conveniência */
  }
}
