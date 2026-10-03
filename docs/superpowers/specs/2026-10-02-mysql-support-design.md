# Suporte a MySQL e MariaDB no SqlDesk

## Objetivo
Permitir conectar a MySQL 8+ e MariaDB com paridade completa em relação ao SQL Server: conectar, executar, cancelar, autocomplete e árvore de objetos, exportar CSV/XLSX e travas de segurança (UPDATE/DELETE sem WHERE, DROP, TRUNCATE, transação com commit/rollback). O comportamento atual do SQL Server não muda.

## Fora do escopo
PostgreSQL ou outros bancos, SSH tunnel, autenticação por socket/Windows, edição de dados direto na grade.

## 1. Camada de provedor
- Nova interface `IDatabaseProvider` no Core, com `SqlServerProvider` (código atual movido) e `MySqlProvider` (MySqlConnector).
- Responsabilidades: criar `DbConnection`, montar/parsear connection string, traduzir erros, carregar metadados, ler versão do servidor, savepoint/transação e fornecer o `ISqlAnalyzer`.
- `TabSessionManager`, `QueryRunner`, `TransactionService`, `MetadataService` e `TransactionHandlers` passam de `SqlConnection`/`SqlException` para `DbConnection`/`DbException`.
- `ConnectionSettings` ganha `Provider` (`SqlServer` | `MySql`). Conexões salvas sem o campo são lidas como `SqlServer`.

## 2. Conexão e interface
- O diálogo de conexão ganha seletor de tipo de servidor, que muda a porta padrão (1433/3306) e as opções avançadas.
- No MySQL, "Confiar no certificado" vira `SslMode` (obrigatório, preferido, desligado). Só usuário e senha.
- Ícone/rótulo do provedor na lista de conexões e na barra de status.

## 3. Analisador do MySQL
- Scanner léxico próprio: respeita strings, comentários, crases, `DELIMITER` e `;`. Separa statements e detecta UPDATE/DELETE sem WHERE, DROP, TRUNCATE e ALTER, reaproveitando `DangerKind`.
- Na dúvida, trata como perigoso (mesmo princípio de hoje para trechos que o ScriptDom não analisa).
- SQL Server continua com o ScriptDom, sem mudança (`ISqlAnalyzer` encapsula o `SqlScriptAnalyzer` atual).

## 4. Travas e transação
- MySQL não tem `OUTPUT`: não há diff por reescrita. Para UPDATE/DELETE perigoso, o app mostra a contagem prévia (`SELECT COUNT(*)` com o mesmo WHERE), executa dentro da transação e confirma ou desfaz, informando `ROW_COUNT()`.
- DDL (DROP, TRUNCATE, ALTER) faz commit implícito no MySQL e não pode ser desfeito: aviso mais forte e sem opção de rollback.
- Tabelas MyISAM não têm transação: aviso quando detectado.
- O C# continua decidindo; o frontend só exibe diálogos.

## 5. Metadados e autocomplete
- `information_schema` (bancos, tabelas, views, colunas, rotinas). No MySQL banco = schema, então a árvore mostra bancos sem o nível extra.
- O backend entrega o formato de metadados normalizado; o frontend recebe o mesmo contrato. Identificadores com crase entram no autocomplete e no formatador.

## 6. Exportação e resultados
- Reaproveitados. `CellValues` ganha mapeamento dos tipos MySQL (`TINYINT(1)`, `DATETIME`, `BLOB`, `JSON`, `DECIMAL` grande).

## 7. Testes
- xUnit: analisador MySQL, provedor, connection string, compatibilidade de conexões salvas antigas.
- Integração real com containers Docker descartáveis (MySQL 8 e MariaDB, porta local, senha de teste não gravada): conectar, executar, cancelar, travas, rollback, exportar. A imagem foi autorizada pelo usuário.
- README atualizado com limitações; imagem do README regenerada se o layout mudar.

## Riscos
- Analisador léxico é menos preciso que o ScriptDom: erra para o lado seguro (falsos positivos), nunca o contrário.
- Refatoração de `SqlConnection` para `DbConnection` toca vários arquivos do Core; os 358 testes .NET e 124 vitest atuais devem continuar passando a cada passo.
