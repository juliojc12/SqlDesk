# SqlDesk

Cliente desktop leve para consultar bancos SQL Server (Windows). Host .NET 8 + WPF com WebView2 carregando um frontend React (Vite, Monaco, Tailwind).

## Estrutura

- `src/SqlDesk.Host` — WPF + WebView2, ponte de mensagens, DI
- `src/SqlDesk.Core` — conexões, execução, metadados, exportação
- `src/SqlDesk.SqlAnalysis` — ScriptDom: separação de statements, travas
- `src/SqlDesk.Web` — React + Vite + Monaco
- `tests/` — testes de Core e SqlAnalysis

## Decisões registradas

(Decisões tomadas onde a especificação era omissa; critério: a opção mais segura para os dados.)

- **Desenvolvimento:** em Debug o WebView2 aponta para `http://localhost:5173` (`npm run dev` em `src/SqlDesk.Web`); a variável `SQLDESK_DEV_URL` sobrescreve a URL.
- **Release:** `dotnet publish src/SqlDesk.Host -c Release` roda `npm run build`, embute `dist/` como recursos e os extrai em `%LOCALAPPDATA%\SqlDesk\web\<hash>`, servidos por `SetVirtualHostNameToFolderMapping` em `https://app.sqldesk`.
- **Barra de título:** a janela usa a moldura padrão do Windows por enquanto; a barra customizada entra na fase de acabamento.
- **Conexões (fase 2):** a senha é gravada com DPAPI (escopo do usuário) e nunca volta ao frontend; o diálogo só recebe `hasPassword`. Senha em branco ao editar mantém a salva; a connection string gerada para uma conexão já salva omite a senha.
- **Só autenticação SQL:** `Integrated Security`/`Trusted_Connection` e `Authentication` diferente de SQL Password são rejeitados ao colar a connection string. Palavras-chave que o SqlClient não conhece também são rejeitadas (mais seguro que descartá-las em silêncio).
- **Timeout de comando** não é uma palavra-chave suportada de forma portável na connection string: fica só no campo e é aplicado ao `SqlCommand`. Se vier `Command Timeout` na string colada, é lido para o campo.
- **`connections.json` corrompido:** o app mostra o erro e não sobrescreve o arquivo.
- **Conectar/desconectar e "nova query" com duplo clique** dependem das sessões por aba e entram na fase 3; nesta fase o duplo clique abre a edição.
- **Aparência (fase 3):** `model-app.png` é a referência visual. O título exibido é "SqlLite Studio" (como no mockup); `SqlDesk` segue como nome interno dos projetos. Tema escuro/claro por tokens CSS (`index.css`), seguindo o sistema.
- **Janela sem moldura:** a barra de título é desenhada pelo frontend (`app-region: drag`); ao maximizar, a janela respeita a área de trabalho do monitor (não cobre a barra de tarefas).
- **Sessões por aba:** uma `SqlConnection` por aba, aberta ao exibir a aba ativa (as demais conectam quando ativadas). Na restauração das abas só se reconecta sozinho quando a senha está salva; sem senha salva o app pergunta (a senha digitada fica só em memória, até fechar o app).
- **Monaco offline:** o editor é empacotado no próprio build (sem CDN), versão fixada em 0.52.2.
- **Salvar `.sql`:** UTF-8 com BOM, para abrir com acentos corretos no SSMS e no Bloco de Notas.
- **Abas e texto:** o estado das abas (incluindo texto não salvo) fica em `%APPDATA%\SqlDesk\session.json`.
- **Análise de SQL (fase 4), tudo em `SqlDesk.SqlAnalysis`, sem regex:** separação por `GO` com um lexer próprio (strings, `[ident]`, `"ident"`, comentários de linha e de bloco aninhados) e detecção dos perigos pela AST do ScriptDom (`TSql160Parser`).
- **Escolhas conservadoras nas travas:** além do que a especificação lista, bloqueia qualquer `DROP` (TRIGGER, SYNONYM, SEQUENCE, TYPE...) e `ALTER TABLE ... DROP COLUMN` (apaga dados); `DROP` de tabela temporária também bloqueia. `WHERE` que não filtra (sem coluna, `1=1`, `id = id`, `id = 5 OR 1=1`) equivale a sem `WHERE`.
- **Trecho que não parseia:** se contém `UPDATE`/`DELETE`/`TRUNCATE`/`DROP` (achados pelos tokens, não por texto), é tratado como perigoso (`Unanalyzable`), sem pré-visualização com `OUTPUT`. Sem essas palavras, só gera o erro de sintaxe.
- **SQL dinâmico:** `EXEC('...')` com literais é analisado por dentro (senão seria um atalho para burlar as travas); SQL montado em tempo de execução e `sp_executesql` só geram **aviso**, porque o conteúdo não pode ser analisado antes de executar. Procedures armazenadas que fazem UPDATE/DELETE internamente também não são vistas.
- **Statement sob o cursor:** o parse é por batch (o `GO` não é T-SQL); se o batch do cursor não parseia, usa o bloco entre linhas em branco ou `;` (este ignora strings e comentários). Erro de sintaxe em *outro* batch não atrapalha. Comentário colado acima de um statement seleciona esse statement.
- **Linhas:** toda linha devolvida é do documento; `Batch.StartLine` permite converter a linha relativa ao batch que o SQL Server reporta.
- **`GO n`:** a contagem fica em `Batch.RepeatCount`; quem executa decide o que fazer (repetir um batch de escrita exige cuidado).
- **Reescrita com `OUTPUT`:** `UPDATE` ganha `OUTPUT deleted.*, inserted.*` e `DELETE` ganha `OUTPUT deleted.*`, gerados pelo `Sql160ScriptGenerator`. **Limitação do SQL Server:** `OUTPUT` sem `INTO` falha (erro 334) em tabela com trigger habilitado; a fase 6 precisa tratar essa falha (desfazer e oferecer execução sem pré-visualização). O formato do statement reescrito muda (fica em uma linha), então linhas de erro posteriores no mesmo batch podem deslocar.
- **Execução e resultados (fase 5):** `ExecutionPlanner` (Core) decide o que rodar a partir do texto, do cursor e da seleção que o frontend envia (seleção exata > statement sob o cursor > script inteiro com F5) e passa o texto por `SqlScriptAnalyzer.Analyze` antes de qualquer comando chegar ao banco. `QueryRunner` executa os batches na conexão da aba, **um comando por vez por aba**, e **para no primeiro erro**.
- **Travas na fase 5:** como o fluxo de dupla confirmação com transação só existe na fase 6, qualquer texto com perigo (`UPDATE`/`DELETE` sem `WHERE`, `TRUNCATE`, `DROP`, trecho não analisável) é **recusado**: nada vai ao banco e as linhas bloqueadas aparecem em Mensagens. `GO n` (repetir batch) também é recusado, pela mesma razão. Erro de sintaxe apontado pelo ScriptDom sem palavra destrutiva não impede a execução: quem decide é o SQL Server.
- **Fluxo de resultados:** a ponte envia eventos `query.started`, `query.resultStarted`, `query.rows` (lotes de 500 linhas, ou antes se o lote demora mais de 200 ms), `query.resultCompleted` e `query.message`; a resposta de `query.execute` chega depois de todos. Cada execução tem um `executionId` e eventos de execuções antigas são descartados. A resposta usa o campo `message` (nunca `error`, que colide com o envelope de erro).
- **Limite de linhas:** 10 mil por result set; o resto é descartado pelo servidor ao passar para o próximo result set. O resultado fica marcado como truncado e "Carregar todas" **reexecuta o trecho que o gerou** (o texto é guardado no momento da execução, mesmo que o editor mude), sem limite, substituindo os resultados da aba.
- **Valores na grade:** `decimal` e `bigint` viajam como texto (o JS só tem `double`); datas em ISO; binário em hexadecimal (64 bytes); texto acima de 50 mil caracteres é cortado na grade (a exportação da fase 8 lê o valor completo).
- **Mensagens:** `PRINT`/avisos (`InfoMessage`), linhas afetadas por statement (`StatementCompleted`, como no SSMS), erros e tempo. A linha do erro vem relativa ao batch e é convertida para o documento; erros dentro de procedures não têm linha clicável, porque a linha é relativa à procedure.
- **Sub-abas:** Ctrl+Enter e F5 substituem os resultados e as mensagens da aba; Ctrl+\ preserva os anteriores e continua a numeração. Clicar numa sub-aba destaca no editor o **batch** que a gerou (não o statement exato: com vários statements no mesmo batch, o destaque cobre o batch inteiro).
- **Grade:** virtualizada (linhas de altura fixa, só as visíveis no DOM), ordenação no cliente com collation `pt-BR` (NULL primeiro; Shift acrescenta colunas), colunas redimensionáveis e reordenáveis, seleção por clique/Shift/arrasto, Ctrl+C em formato tabulado (NULL vira vazio; Ctrl+Shift+C ou a opção copia com cabeçalho).
- **Cancelamento:** Esc ou botão Parar cancelam via `CancellationToken` (o SqlClient envia o cancelamento ao servidor); fechar ou desconectar a aba também cancela. Esc só é consumido quando há execução em andamento.
