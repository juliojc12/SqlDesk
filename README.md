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
