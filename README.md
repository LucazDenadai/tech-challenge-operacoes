# tech-challenge-operacoes

Serviço **Operações** da Fase 4 do Tech Challenge (oficina mecânica). Junta dois módulos com ownership único: **Estoque** (catálogo de peças e serviços, preços de tabela, saldos por filial, reservas e movimentações) e **Execução** (diagnóstico, fila e reparo, no CARD-38b).

Decisões e contratos ficam em [tech-challenge-docs](https://github.com/LucazDenadai/tech-challenge-docs): limites e ownership ([ADR-014](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-014-limites-microsservicos-fase4.md), [ADR-015](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-015-ownership-e-infraestrutura-fase4.md)), bancos ([ADR-016](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-016-bancos-sql-nosql-fase4.md)), Saga ([ADR-017](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-017-saga-orquestrada-os-fase4.md)) e contratos assíncronos ([ADR-018](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-018-contratos-assincronos-asyncapi-fase4.md)). Escopo e decisões de implementação: [CARD-38](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-38-servico-operacoes.md) e [CARD-38a](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-38a-estoque-em-operacoes.md).

## Estrutura

```
src/OficinaMecanica.Operacoes.Domain          catálogo (Peca, Servico) e estoque (saldo, reserva, movimentação)
src/OficinaMecanica.Operacoes.Application     casos de uso, portas e contratos da Saga
src/OficinaMecanica.Operacoes.Infrastructure  EF Core/PostgreSQL, inbox, outbox, consumidor e despachante RabbitMQ
src/OficinaMecanica.Operacoes.API             controllers, autenticação, Swagger, health checks
tests/                                        testes unitários, de contrato e de integração
contratos/asyncapi-saga-os.yaml               cópia da spec AsyncAPI usada nos testes de contrato
docs/openapi/operacoes-v1.json                OpenAPI gerado pela API
```

Execução usa o Estoque só pela porta `IEstoqueParaExecucao` (consumir e registrar falha), nunca pelas tabelas.

## Configuração

Nenhum segredo é versionado. Em execução, todos chegam por variável de ambiente (`Secao__Chave`) ou Secret do Kubernetes.

| Chave | Uso |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL exclusivo de Operações. Migrations e seed rodam no start. |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Validação do JWT emitido pelo OS. Mesma chave e issuer do OS. |
| `RabbitMq__Enabled`, `__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | Consumidor dos comandos e despachante da outbox. Habilitado, exige usuário e senha. |
| `RabbitMq__IntervaloOutboxMs` | Intervalo de leitura da outbox (padrão 500 ms). |
| `Cors__Origins__0` | Origem permitida. Obrigatória fora de `Development`/`Test`. |
| `Jaeger__Endpoint` | Coletor OTLP/HTTP dos traces. |

## Executar

```sh
docker run -d --name operacoes-postgres -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:16-alpine

ConnectionStrings__DefaultConnection="Host=localhost;Port=5433;Database=oficina_operacoes;Username=postgres;Password=postgres" \
Jwt__Key="<mesma chave do OS, 32+ caracteres>" \
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/OficinaMecanica.Operacoes.API
```

Swagger em `/operacoes/swagger`, liveness em `/operacoes/health` e readiness (banco e, se habilitado, RabbitMQ) em `/operacoes/ready`. O token vem de `POST /os/auth/login` no serviço OS.

O seed cria a filial `FILIAL-DEMO` com o mesmo `Id` do seed do OS (`378aeb39-37f6-43c1-9526-5b1a9fadd553`), 5 peças com saldo nessa filial e 4 serviços. A vela de ignição fica sem saldo, para demonstrar a reserva recusada.

## API

| Rota | Quem | O que faz |
|---|---|---|
| `GET /operacoes/pecas`, `/servicos` | funcionários | Catálogo com preço de tabela (BRL). `?incluirInativas=true` / `?incluirInativos=true` |
| `POST`, `PUT`, `DELETE /operacoes/pecas`, `/servicos` | Admin | Cadastro. `DELETE` desativa, não apaga. Código de peça repetido devolve `422`. |
| `GET /operacoes/estoque/filiais` | funcionários | Filiais onde Operações opera estoque |
| `GET /operacoes/estoque/filiais/{id}/saldos` | funcionários | Disponível e reservado por peça |
| `POST /operacoes/estoque/disponibilidade` | funcionários | Consulta disponibilidade. Não reserva. |
| `GET /operacoes/estoque/filiais/{id}/movimentacoes` | Admin, Atendente | Filtros `pecaId`, `osId`, `correlationId`, `limite` |
| `POST /operacoes/estoque/entradas`, `/ajustes` | Admin | Entrada de peças e ajuste de inventário |

Reserva, consumo e liberação não têm rota: vêm só pelos comandos da Saga. Gravação concorrente sobre o mesmo saldo devolve `409`. Todas as respostas devolvem o cabeçalho `X-Correlation-Id`, recebido ou gerado.

## Mensageria

Operações é participante da Saga orquestrada pelo OS (ADR-017). Para cada comando que consome, declara a fila `operacoes.<canal>` e a DLQ `operacoes.<canal>.dlq` (ADR-018).

| Comando | Efeito | Evento publicado |
|---|---|---|
| `InventoryReservationRequested.v1` | Reserva tudo ou nada por `(filialId, pecaId)` | `InventoryReserved.v1` ou `InventoryReservationRejected.v1` com `unavailablePecaIds` |
| `InventoryReleaseRequested.v1` | Devolve ao disponível o que não foi consumido | `InventoryReleased.v1` |

`DiagnosisRequested` e `ExecutionStartRequested` entram no CARD-38b.

Cada comando é processado em uma transação do PostgreSQL com três gravações: a inbox (deduplicação por `messageId`), o efeito no estoque e o evento de resultado na outbox (ADR-016). Ou as três ficam, ou nenhuma.

- **Duplicata:** a mensagem é confirmada sem novo efeito nem novo evento. A mesma `idempotencyKey` em outra mensagem devolve a reserva já feita.
- **Fora do contrato ou referência inexistente:** vai para a DLQ com o cabeçalho `x-motivo`.
- **Falha transitória** (incluindo conflito de versão do saldo): 3 novas tentativas (1 s, 5 s, 10 s) antes da DLQ. Cada tentativa relê o saldo.

O despachante lê a outbox, publica na exchange do canal com confirmação do broker e marca a mensagem. A entrega é ao menos uma vez: se cair entre publicar e marcar, publica de novo, e o OS deduplica pelo `messageId`. O `traceparent` do comando segue no evento, então comando e resposta ficam no mesmo trace.

## Testes

```sh
dotnet test tests/OficinaMecanica.Operacoes.UnitTests          # domínio, casos de uso e contratos AsyncAPI
dotnet test tests/OficinaMecanica.Operacoes.IntegrationTests   # PostgreSQL e RabbitMQ via Testcontainers (Docker)
```

Com `EXPORTAR_OPENAPI=<caminho>` definido, o teste de Swagger grava o OpenAPI gerado (fonte de `docs/openapi/operacoes-v1.json`).
