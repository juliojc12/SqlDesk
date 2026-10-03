export type ObjectType = 'table' | 'view' | 'procedure' | 'function'

export interface MetaObject {
  schema: string
  name: string
  type: ObjectType
}

export interface MetaColumn {
  name: string
  type: string
  nullable: boolean
}

/** Resposta de `metadata.get`. */
export interface MetadataDto {
  loaded: boolean
  columnsLoaded: boolean
  loading: boolean
  schemas: string[]
  objects: MetaObject[]
  columns: Record<string, MetaColumn[]>
  /** false no MySQL/MariaDB: o primeiro nível são os bancos e não há schema (ausente vale true). */
  hasSchemaLevel?: boolean
}

const lower = (s: string) => s.toLowerCase()

/** Índices em memória sobre o cache de metadados, para o autocomplete e a árvore de objetos. */
export class MetaIndex {
  readonly schemas: string[]
  readonly objects: MetaObject[]
  readonly columnsLoaded: boolean
  readonly hasSchemaLevel: boolean
  /** Banco atual da conexão: sem nível de schema, é ele quem desempata nomes iguais (no lugar de `dbo`). */
  defaultSchema: string | undefined
  private readonly byName = new Map<string, MetaObject[]>()
  private readonly bySchema = new Map<string, MetaObject[]>()
  private readonly columns = new Map<string, MetaColumn[]>()

  constructor(dto: MetadataDto, defaultSchema?: string) {
    this.schemas = dto.schemas
    this.objects = dto.objects
    this.columnsLoaded = dto.columnsLoaded
    this.hasSchemaLevel = dto.hasSchemaLevel !== false
    this.defaultSchema = defaultSchema
    for (const o of dto.objects) {
      push(this.byName, lower(o.name), o)
      push(this.bySchema, lower(o.schema), o)
    }
    for (const [key, cols] of Object.entries(dto.columns)) this.columns.set(lower(key), cols)
  }

  hasSchema(name: string): boolean {
    return this.bySchema.has(lower(name))
  }

  /** Nome de schema como está no banco (caixa original), ou undefined. */
  schemaName(name: string): string | undefined {
    return this.bySchema.get(lower(name))?.[0].schema
  }

  objectsOfSchema(schema: string): MetaObject[] {
    return this.bySchema.get(lower(schema)) ?? []
  }

  /** O schema cujos objetos se escrevem sem qualificação: `dbo` no SQL Server, o banco atual da conexão no MySQL. */
  isDefaultSchema(name: string): boolean {
    const preferred = this.hasSchemaLevel ? 'dbo' : this.defaultSchema
    return !!preferred && lower(name) === lower(preferred)
  }

  /**
   * Acha um objeto pelo nome, com ou sem schema. Sem schema e com homônimos em schemas diferentes, prefere `dbo` (ou, sem nível de schema, o banco atual da conexão).
   * `types` restringe o tipo (por padrão, tabelas, views e funções, que têm colunas).
   */
  find(schema: string | undefined, name: string, types: readonly ObjectType[] = ['table', 'view', 'function']): MetaObject | undefined {
    const candidates = (this.byName.get(lower(name)) ?? []).filter((o) => types.includes(o.type))
    if (schema) return candidates.find((o) => lower(o.schema) === lower(schema))
    return candidates.find((o) => this.isDefaultSchema(o.schema)) ?? candidates[0]
  }

  columnsOf(o: MetaObject): MetaColumn[] {
    return this.columns.get(lower(`${o.schema}.${o.name}`)) ?? []
  }
}

function push<K, V>(map: Map<K, V[]>, key: K, value: V) {
  const list = map.get(key)
  if (list) list.push(value)
  else map.set(key, [value])
}

export const buildIndex = (dto: MetadataDto, defaultSchema?: string) => new MetaIndex(dto, defaultSchema)
