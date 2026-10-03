# Bancos de teste (MySQL e MariaDB)

Containers descartáveis (dados em tmpfs) só para os testes de integração.

    $env:SQLDESK_TEST_PWD = "<senha descartável>"   # PowerShell; não grave em arquivo
    docker compose -f tests/docker/docker-compose.yml up -d
    dotnet test tests/SqlDesk.Integration.Tests
    docker compose -f tests/docker/docker-compose.yml down

Sem `SQLDESK_TEST_PWD` ou com um dos containers fora do ar, os testes de integração aparecem como pulados (não falham).

## Transação aberta: o que cada servidor responde

- MariaDB 11: `SELECT @@in_transaction` vale 1 logo depois do `START TRANSACTION` (e do `BEGIN`), sem privilégio extra.
- MySQL 8.4: não tem `@@in_transaction`, e um usuário só com privilégios no próprio banco recebe erro 1142 ao consultar
  `performance_schema.events_transactions_current` (com privilégio, ela mostra `ACTIVE` logo depois do `START TRANSACTION`).
  O app usa um savepoint de sonda (`SAVEPOINT` + `RELEASE SAVEPOINT`): fora de transação o `RELEASE` falha com 1305.
