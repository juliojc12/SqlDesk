<p align="center"><img src="docs/icon.png" alt="Ícone do SqlLite Studio" width="112"></p>

# SqlLite Studio

Cliente desktop leve para consultar bancos **SQL Server, MySQL e MariaDB**, só para Windows. Interface moderna em tema escuro, execução no estilo do DBeaver e travas contra comandos destrutivos.

![SqlLite Studio](docs/screenshot.png)

## Funcionalidades

- **Várias conexões ao mesmo tempo**, com autenticação por usuário e senha, cada uma com a sua cor e o tipo do banco ao lado do nome. O diálogo de conexão tem o seletor **Tipo de servidor** (SQL Server, MySQL ou MariaDB). A senha é guardada criptografada (DPAPI) e nunca volta para a interface.
- **Abas de query coloridas** pela conexão, com restauração das abas ao reabrir o app.
- **Editor Monaco** com autocomplete contextual (tabelas, colunas, schemas ou bancos, procedures) e alias automático de tabela.
- **Formatação do SQL** (`Shift+Alt+F` ou botão Formatar): da seleção ou do texto todo, preservando comentários e textos entre aspas, e só aceita o resultado se o conteúdo do script não mudar.
- **Execução como no DBeaver:** `Ctrl+Enter` executa a seleção ou o statement sob o cursor, `Ctrl+\` abre o resultado numa nova sub-aba, `F5` executa o script inteiro (com `GO`) e `Esc` cancela.
- **Resultados** em grade virtualizada: ordenação pela setinha do cabeçalho, clique no título para selecionar a coluna inteira, colunas redimensionáveis e reordenáveis, cópia para o Excel, vários result sets e aba de mensagens.
- **Travas de segurança:** `UPDATE` e `DELETE` sem `WHERE`, `TRUNCATE` e `DROP` são travados. No SQL Server exigem dupla confirmação: o comando roda dentro de uma transação, mostra o antes e o depois, e só então você escolhe Commit ou Rollback. No MySQL e no MariaDB, `UPDATE`/`DELETE` sem `WHERE` rodam em transação com contagem prévia das linhas afetadas (em tabelas InnoDB); já o que o servidor confirma sozinho (DDL como `DROP`, `TRUNCATE`, `ALTER`, `CREATE`, e comandos como `COMMIT` ou `START TRANSACTION`) exige **uma única confirmação, mais forte**, avisando que não dá para desfazer, e **roda direto, sem Commit nem Rollback depois**. O mesmo vale para `UPDATE`/`DELETE` sem `WHERE` numa tabela que não guarda as alterações em transação (MyISAM, Aria, MEMORY, ou uma view): o app consulta o mecanismo da tabela antes de pedir a confirmação e, se não conseguir conferir, também trata como irreversível. Se um `ROLLBACK` da trava não desfizer alguma tabela não transacional (por exemplo, alterada por outro comando do mesmo trecho), o app avisa em vez de dizer que desfez.
- **Transações:** recomendação de `TRANSACTION` para comandos de escrita, indicador de transação aberta nas abas e decisão ao fechar a aba ou o app.
- **Exportação** em CSV e XLSX, em streaming, respeitando a ordenação atual; resultados grandes podem ser reexecutados e exportados por inteiro.
- **Árvore de objetos** com schemas, tabelas, views e procedures; cada tabela e view expande para mostrar os **campos**, com o tipo e se aceitam NULL. No MySQL e no MariaDB não há nível de schema: o primeiro nível são os **bancos**. Duplo clique abre um `SELECT TOP 100` da tabela (no MySQL e no MariaDB, `SELECT * ... LIMIT 100`).

## MySQL e MariaDB

**Como conectar:** escolha o **Tipo de servidor** no diálogo e informe `host` ou `host:porta` (a porta padrão é 3306). As duas opções de segurança viram o `SslMode` do driver: sem "Criptografar a conexão", `None`; criptografando e aceitando o certificado não verificado, `Required`; criptografando sem aceitar, `VerifyCA`.

**Limitações conhecidas:**

- O analisador de SQL do MySQL é **léxico** (não há um parser como o ScriptDom): na dúvida, trata o trecho como perigoso. `PREPARE ... FROM @var` e `CALL` não são inspecionados por dentro.
- A reexecução para exportar aceita `SELECT` que chama funções (`SELECT f()`, `GET_LOCK`, `NEXTVAL`): no MySQL uma função armazenada pode gravar dados, e a reexecução roda a consulta de novo.
- A checagem do mecanismo de armazenamento olha a tabela-alvo de cada `UPDATE`/`DELETE` perigoso. Gravações em tabela não transacional que ela não vê (trigger, `UPDATE` com `JOIN`, outro comando do mesmo trecho) só são percebidas no `ROLLBACK`, pelo aviso do servidor.
- Só autenticação por usuário e senha: na connection string, certificado de cliente (`CertificateFile`, `SslCert`, `SslKey`...), arquivos de CA ou de chave, socket/pipe/memória compartilhada (`ConnectionProtocol`, `PipeName`), Kerberos (`ServerSPN`) e `AllowLoadLocalInfile` são recusados; o TLS vem das duas caixas do diálogo.
- O formatador **recusa formatar** quando não tem certeza de preservar o script, por exemplo com comentários executáveis `/*! ... */`; o texto fica como estava.
- O autocomplete usa as palavras-chave e os snippets do T-SQL, além de tabelas, colunas e bancos reais.
- No MariaDB os tipos inteiros aparecem como `int(11)`.
- `BIGINT UNSIGNED` no Excel perde dígitos acima de 2^53 (o Excel guarda números como `double`); no CSV sai completo. Datas zero (`0000-00-00`) aparecem como texto.

**Testes de integração:** rodam contra containers descartáveis, e **sem a variável de ambiente `SQLDESK_TEST_PWD` (ou com um container fora do ar) aparecem como pulados**, sem falhar:

```powershell
$env:SQLDESK_TEST_PWD = "<senha descartável>"   # não grave em arquivo
docker compose -f tests/docker/docker-compose.yml up -d
dotnet test tests/SqlDesk.Integration.Tests
docker compose -f tests/docker/docker-compose.yml down
```

## Como executar

**Requisitos:** Windows 10 ou 11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Node.js](https://nodejs.org) (versão LTS) e o [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (já vem no Windows 10 e 11).

**Em desenvolvimento** (dois terminais, na raiz do repositório):

```powershell
# 1. frontend (Vite)
cd src/SqlDesk.Web
npm install
npm run dev

# 2. aplicativo
dotnet run --project src/SqlDesk.Host
```

**Gerar o executável único:**

```powershell
dotnet publish src/SqlDesk.Host -c Release -o publish
```

O resultado é um único `publish/SqlDesk.exe`, que já leva o runtime e o frontend embutidos.

**Testes:**

```powershell
dotnet test
cd src/SqlDesk.Web; npm test
```

## Estrutura

| Pasta | Conteúdo |
| --- | --- |
| `src/SqlDesk.Host` | Janela WPF com WebView2 e ponte de mensagens |
| `src/SqlDesk.Core` | Conexões, execução, metadados, transações e exportação |
| `src/SqlDesk.SqlAnalysis` | Análise de T-SQL (ScriptDom) e de MySQL (léxica): statements, travas e reescrita com `OUTPUT` |
| `src/SqlDesk.Web` | Frontend React, Vite e Monaco |
| `tests` | Testes de Core e SqlAnalysis; `tests/SqlDesk.Integration.Tests` e `tests/docker` para MySQL e MariaDB |

Os dados do usuário (conexões e sessão) ficam em `%APPDATA%\SqlDesk`. Decisões de projeto e limitações estão em [`docs/decisoes.md`](docs/decisoes.md).
