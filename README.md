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
