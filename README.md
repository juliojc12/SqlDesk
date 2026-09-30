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
