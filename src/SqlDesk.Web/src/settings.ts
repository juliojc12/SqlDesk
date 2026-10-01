/**
 * Configurações do usuário, guardadas no armazenamento do WebView (um JSON). Valores inválidos ou fora da faixa voltam ao
 * padrão ou são ajustados aos limites: nada que venha de lá chega ao backend sem passar por `normalize`.
 */

export type CsvDelimiter = ';' | ','

export interface AppSettings {
  /** Linhas por result set carregadas na grade; "Carregar todas" ignora o limite. */
  maxRows: number
  /** Alias automático de tabela no autocomplete (FROM e JOIN). */
  autoAlias: boolean
  csvDelimiter: CsvDelimiter
  /** Timeout de comando (segundos, 0 = sem limite) proposto ao criar uma conexão nova; cada conexão guarda o seu. */
  commandTimeout: number
}

export const MIN_ROWS = 100
export const MAX_ROWS = 1_000_000
export const MAX_TIMEOUT = 86_400

export const DEFAULT_SETTINGS: AppSettings = { maxRows: 10_000, autoAlias: true, csvDelimiter: ';', commandTimeout: 30 }

const KEY = 'sqldesk.settings'

const clampInt = (v: unknown, min: number, max: number, fallback: number): number => {
  const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN
  return Number.isFinite(n) ? Math.min(max, Math.max(min, Math.round(n))) : fallback
}

/** Aceita qualquer coisa (JSON antigo, valor digitado) e devolve configurações válidas. */
export function normalize(raw: unknown): AppSettings {
  const o = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>
  return {
    maxRows: clampInt(o.maxRows, MIN_ROWS, MAX_ROWS, DEFAULT_SETTINGS.maxRows),
    autoAlias: typeof o.autoAlias === 'boolean' ? o.autoAlias : DEFAULT_SETTINGS.autoAlias,
    csvDelimiter: o.csvDelimiter === ',' ? ',' : ';',
    commandTimeout: clampInt(o.commandTimeout, 0, MAX_TIMEOUT, DEFAULT_SETTINGS.commandTimeout),
  }
}

export function loadSettings(): AppSettings {
  try {
    const text = localStorage.getItem(KEY)
    return normalize(text ? JSON.parse(text) : {})
  } catch {
    return { ...DEFAULT_SETTINGS }
  }
}

export function saveSettings(s: AppSettings): AppSettings {
  const clean = normalize(s)
  try {
    localStorage.setItem(KEY, JSON.stringify(clean))
  } catch {
    /* sem armazenamento, as configurações valem só até fechar o app */
  }
  cache = clean
  return clean
}

let cache: AppSettings | null = null

const current = () => (cache ??= loadSettings())

export const getAutoAlias = () => current().autoAlias
export const getCsvDelimiter = () => current().csvDelimiter
export const getMaxRows = () => current().maxRows
export const getDefaultCommandTimeout = () => current().commandTimeout
