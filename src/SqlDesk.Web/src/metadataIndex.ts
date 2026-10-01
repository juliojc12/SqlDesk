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
}

const lower = (s: string) => s.toLowerCase()

/** Índices em memória sobre o cache de metadados, para o autocomplete e a árvore de objetos. */
export class MetaIndex {
  readonly schemas: string[]
  readonly objects: MetaObject[]
  readonly columnsLoaded: boolean
  private readonly byName = new Map<string, MetaObject[]>()
  private readonly bySchema = new Map<string, MetaObject[]>()
  private readonly columns = new Map<string, MetaColumn[]>()

  constructor(dto: MetadataDto) {
    this.schemas = dto.schemas
    this.objects = dto.objects
    this.columnsLoaded = dto.columnsLoaded
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

  /**
   * Acha um objeto pelo nome, com ou sem schema. Sem schema e com homônimos em schemas diferentes, prefere `dbo`.
   * `types` restringe o tipo (por padrão, tabelas, views e funções, que têm colunas).
   */
  find(schema: string | undefined, name: string, types: readonly ObjectType[] = ['table', 'view', 'function']): MetaObject | undefined {
    const candidates = (this.byName.get(lower(name)) ?? []).filter((o) => types.includes(o.type))
    if (schema) return candidates.find((o) => lower(o.schema) === lower(schema))
    return candidates.find((o) => lower(o.schema) === 'dbo') ?? candidates[0]
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

export const buildIndex = (dto: MetadataDto) => new MetaIndex(dto)
