# tech-challenge-operacoes

Serviço **Operações** da Fase 4 do Tech Challenge (oficina mecânica). Junta dois módulos com ownership único:

- **Estoque**, no PostgreSQL: catálogo de peças e serviços, preços de tabela, saldos por filial, reservas e movimentações.
- **Execução**, no DynamoDB: diagnóstico, fila, reparo, conclusão e falha.

Decisões e contratos ficam em [tech-challenge-docs](https://github.com/LucazDenadai/tech-challenge-docs): limites e ownership ([ADR-014](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-014-limites-microsservicos-fase4.md), [ADR-015](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-015-ownership-e-infraestrutura-fase4.md)), bancos ([ADR-016](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-016-bancos-sql-nosql-fase4.md)), Saga ([ADR-017](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-017-saga-orquestrada-os-fase4.md)) e contratos assíncronos ([ADR-018](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/adr/ADR-018-contratos-assincronos-asyncapi-fase4.md)). Escopo e decisões de implementação: [CARD-38](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-38-servico-operacoes.md), [CARD-38a](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-38a-estoque-em-operacoes.md) e [CARD-38b](https://github.com/LucazDenadai/tech-challenge-docs/blob/main/cards/06-fase4-microsservicos-saga/CARD-38b-fila-execucao.md).

## Estrutura

```
src/OficinaMecanica.Operacoes.Domain          catálogo, estoque (saldo, reserva, movimentação) e execução
src/OficinaMecanica.Operacoes.Application     casos de uso, portas e contratos da Saga
src/OficinaMecanica.Operacoes.Infrastructure  EF Core/PostgreSQL, DynamoDB, inbox, outboxes, consumidor e despachantes RabbitMQ
src/OficinaMecanica.Operacoes.API             controllers, autenticação, Swagger, health checks
tests/                                        testes unitários, de contrato e de integração
contratos/asyncapi-saga-os.yaml               cópia da spec AsyncAPI usada nos testes de contrato
docs/openapi/operacoes-v1.json                OpenAPI gerado pela API
docker-compose.yml                            PostgreSQL, RabbitMQ, DynamoDB Local e Jaeger para desenvolvimento
```

A Execução usa o Estoque só pela porta `IEstoqueParaExecucao` (filial, reserva, consumo), e o Catálogo só pela `ICatalogoParaExecucao` (preço vigente). Nunca acessa as tabelas deles.

## Configuração

Nenhum segredo é versionado. Em execução, todos chegam por variável de ambiente (`Secao__Chave`) ou Secret do Kubernetes.

| Chave | Uso |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL exclusivo de Operações. Migrations e seed rodam no start. |
| `DynamoDb__Tabela` | Tabela da execução (padrão `operacoes-execucoes`). |
| `DynamoDb__ServiceUrl`, `__AccessKey`, `__SecretKey` | Só no DynamoDB Local: endpoint e credenciais fictícias. Vazios na nuvem, onde valem a região e o IAM. |
| `DynamoDb__Regiao` | Região AWS (padrão `us-east-1`). |
| `DynamoDb__CriarTabela` | `true` cria a tabela e o índice no start. Só em desenvolvimento e testes; na nuvem a tabela vem da IaC. |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Validação do JWT emitido pelo OS. Mesma chave e issuer do OS. |
| `RabbitMq__Enabled`, `__Host`, `__Port`, `__VirtualHost`, `__Username`, `__Password` | Consumidor dos comandos e despachantes das outboxes. Habilitado, exige usuário e senha. |
| `RabbitMq__IntervaloOutboxMs` | Intervalo de leitura das outboxes (padrão 500 ms). |
| `Cors__Origins__0` | Origem permitida. Obrigatória fora de `Development`/`Test`. |
| `Jaeger__Endpoint` | Coletor OTLP/HTTP dos traces. |

## Executar

```sh
docker compose up -d

ConnectionStrings__DefaultConnection="Host=localhost;Port=5433;Database=oficina_operacoes;Username=postgres;Password=postgres" \
DynamoDb__ServiceUrl=http://localhost:8000 DynamoDb__AccessKey=local DynamoDb__SecretKey=local DynamoDb__CriarTabela=true \
RabbitMq__Enabled=true RabbitMq__Username=operacoes RabbitMq__Password=operacoes-local \
Jwt__Key="<mesma chave do OS, 32+ caracteres>" \
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/OficinaMecanica.Operacoes.API
```

O DynamoDB Local não acessa a AWS e não gera cobrança. As credenciais `local`/`local` são fictícias.

Swagger em `/operacoes/swagger`, liveness em `/operacoes/health` e readiness (PostgreSQL, DynamoDB e, se habilitado, RabbitMQ) em `/operacoes/ready`. O token vem de `POST /os/auth/login` no serviço OS.

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
| `GET /operacoes/execucoes?filialId=&status=` | funcionários | Fila da filial em um estado |
| `GET /operacoes/execucoes/{id}`, `/os/{osId}` | funcionários | Execução com itens, etapas, consumo e histórico |
| `POST /operacoes/execucoes/{id}/diagnostico`, `/diagnostico/rejeicao` | Mecânico, Admin | Diagnóstico com preço do catálogo, ou rejeição com motivo |
| `POST /operacoes/execucoes/{id}/reparo`, `/etapas` | Mecânico, Admin | Início do reparo e progresso. Sem evento para o OS. |
| `POST /operacoes/execucoes/{id}/conclusao`, `/falha` | Mecânico, Admin | Encerra informando as peças consumidas |

Reserva, consumo, liberação e o início da execução não têm rota: vêm só pelos comandos da Saga. Transição fora de ordem devolve `422`. Gravação concorrente sobre o mesmo saldo ou a mesma execução devolve `409`. Todas as respostas devolvem o cabeçalho `X-Correlation-Id`, recebido ou gerado.

Estados da execução: `EmDiagnostico` → `Diagnosticada` → `NaFila` → `EmReparo` → `Concluida` ou `Falhou`. `DiagnosticoRejeitado` encerra no início. Cada transição fica no histórico com responsável, data e motivo.

## Mensageria

Operações é participante da Saga orquestrada pelo OS (ADR-017). Para cada comando que consome, declara a fila `operacoes.<canal>` e a DLQ `operacoes.<canal>.dlq` (ADR-018).

| Comando | Efeito | Evento publicado |
|---|---|---|
| `InventoryReservationRequested.v1` | Reserva tudo ou nada por `(filialId, pecaId)` | `InventoryReserved.v1` ou `InventoryReservationRejected.v1` com `unavailablePecaIds` |
| `InventoryReleaseRequested.v1` | Devolve ao disponível o que não foi consumido | `InventoryReleased.v1` |
| `DiagnosisRequested.v1` | Abre a execução da OS (uma por OS). Filial onde Operações não opera é rejeitada. | Nenhum até o técnico registrar. Na rejeição automática, `DiagnosisRejected.v1`. |
| `ExecutionStartRequested.v1` | Põe na fila a execução diagnosticada com reserva ativa da mesma OS | `ExecutionStarted.v1` ou `ExecutionStartRejected.v1` |

Pela API, o técnico gera `DiagnosisCompleted.v1` (itens com o preço do catálogo), `DiagnosisRejected.v1`, `ExecutionCompleted.v1` e `ExecutionFailed.v1`.

Cada comando é gravado de uma vez no store do seu módulo: inbox (deduplicação por `messageId`), efeito e evento de resultado na outbox (ADR-016). Ou tudo fica, ou nada.

- **Estoque:** uma transação do PostgreSQL.
- **Execução:** um `TransactWriteItems` no DynamoDB, com condição de versão no agregado e de inexistência na inbox e na outbox.

Na conclusão ou falha, o consumo da reserva vai primeiro para o PostgreSQL (idempotente). Depois, o agregado e o evento vão para o DynamoDB. Não há transação entre os dois stores, então se a segunda gravação falhar, a chamada pode ser repetida sem consumir de novo.

- **Duplicata:** a mensagem é confirmada sem novo efeito nem novo evento. A mesma `idempotencyKey` em outra mensagem devolve a reserva ou o início já feitos.
- **Fora do contrato ou referência inexistente:** vai para a DLQ com o cabeçalho `x-motivo`.
- **Falha transitória** (incluindo conflito de versão ou timeout do DynamoDB): 3 novas tentativas (1 s, 5 s, 10 s) antes da DLQ. Cada tentativa relê o estado.

Dois despachantes, um por outbox, publicam com o mesmo publicador: exchange do canal, confirmação do broker, mensagem marcada depois. A entrega é ao menos uma vez: se cair entre publicar e marcar, publica de novo, e o OS deduplica pelo `messageId`. O `traceparent` do comando segue no evento, então comando e resposta ficam no mesmo trace.

### Tabela DynamoDB

Uma tabela, chave `PK`/`SK`, e o índice `GSI1` para a fila e a outbox pendente:

| Item | PK | SK | GSI1PK |
|---|---|---|---|
| Execução | `EXEC#<executionId>` | `EXEC` | `FILIAL#<filialId>#STATUS#<status>` |
| Execução da OS | `OS#<osId>` | `EXEC` | — |
| Inbox | `INBOX#<messageId>` | `INBOX` | — |
| Outbox | `OUTBOX#<messageId>` | `OUTBOX` | `OUTBOX#PENDENTE`, removido ao publicar |

## Testes

```sh
dotnet test tests/OficinaMecanica.Operacoes.UnitTests          # domínio, casos de uso e contratos AsyncAPI
dotnet test tests/OficinaMecanica.Operacoes.IntegrationTests   # PostgreSQL, RabbitMQ e DynamoDB Local via Testcontainers (Docker)
```

Os testes de integração não usam conta nem credencial AWS: o DynamoDB Local roda em container, com tabela própria por teste.

Com `EXPORTAR_OPENAPI=<caminho>` definido, o teste de Swagger grava o OpenAPI gerado (fonte de `docs/openapi/operacoes-v1.json`).
