# RabbitMQ и MassTransit — общая спецификация

**Комплект:** этот файл + `plan/README.md`. README содержит полные C# records, сигнатуры существующих библиотек, версии пакетов и общие правила. Этот документ определяет задачи владельца транспорта и проверки совместимости всех сервисов.

**Статус:** `HabitTracker.Contracts`, `HabitTracker.Messaging`, JSON-топология и тест публикации WebAPI уже реализованы. Consumers Worker/Notifications, их retry/DLQ и эксплуатационные процедуры ещё нужно подключить и проверить. Не заменять существующий outbox другой схемой без отдельного решения команды.

**Цель** — закрепить контракты и обеспечить асинхронную доставку от WebAPI к Worker и от Worker к Notifications.

Реализация и тесты входят в планы [WebAPI](01-webapi.md), [Worker](02-worker.md) и [Notifications](03-notifications.md). Записи сообщений на C# находятся в общем проекте `Auth/HabitTracker.Contracts`. Настройки брокера передаются через `RabbitMq__Host`, `RabbitMq__VirtualHost`, `RabbitMq__Username`, `RabbitMq__Password`.

## Контракты v1

Общие поля — `eventId` (UUID), `occurredAtUtc`, `correlationId` (UUID). `MessageId` и `CorrelationId` MassTransit получают те же значения. `traceparent` и при наличии `tracestate` передаются как заголовки. ID MongoDB передаются строками, даты — `YYYY-MM-DD`, время расписания — `HH:mm`, моменты времени — ISO 8601 UTC. Изменения смысла или обязательных полей выпускаются в новой версии.

| Контракт | Издатель | Полезная нагрузка |
|---|---|---|
| `HabitChangedV1` | WebAPI | `userId`, `habitId`, `revision`, `change` (`created`, `edited`, `archived`, `reminderChanged`, `completionChanged`), `status`, `startDate`, `scheduleVersions[]` с `effectiveFrom` и `daysOfWeek`, `timeZoneId`, `reminderEnabled`, `reminderLocalTime` |
| `SummarySettingsChangedV1` | WebAPI | `userId`, `revision`, `enabled`, `localTime`, `timeZoneId` |
| `NotificationReadyV1` | Worker | `notificationId` (UUID), `userId`, `kind` (`habitReminder` или `dailySummary`), `localDate`, `createdAtUtc`, `habitId` и `habitName` для напоминания, `planned`, `completed`, `pending` для сводки |

События расписаний содержат полное состояние на указанной версии. Worker применяет версии монотонно. Данные входа, секреты и токены в сообщениях отсутствуют.

## Топология

Virtual host `habit-tracker`. Явно задать имя exchange каждого контракта через MassTransit и имена receive endpoint. Тип exchange `fanout`, очереди и exchanges durable, auto-delete выключен. Для одного узла учебного стенда используются classic queues. Consumer binding связывает exchange контракта с одноимённым exchange receive endpoint, тот связан с очередью.

| Exchange контракта | Exchange endpoint и очередь | Потребитель |
|---|---|---|
| `habit-changed-v1` | `worker-habit-events-v1` | Worker |
| `summary-settings-changed-v1` | `worker-habit-events-v1` | Worker |
| `notification-ready-v1` | `notifications-ready-v1` | Notifications |

Топология создаётся до запуска издателей. Порядок подготовки и проверки брокера описан в [плане развёртывания](05-deployment.md).

## Outbox

WebAPI и Worker записывают событие через сессию транзакции владельца данных. Поля `eventId`, `contract`, `payload`, заголовки, `status`, `attempts`, `lastUpdated`, `nextAttemptAt`, `leaseOwner`, `leaseUntil`, `leaseVersion`, `publishedAt`. Уникальный `_id` совпадает с `eventId`. Индексы `(status, nextAttemptAt, _id)` и `(status, leaseUntil, _id)` обслуживают отправку и восстановление.

Издатель атомарно захватывает запись и выставляет `published` после подтверждения брокера. Задержка повторов растёт до пяти минут. Метрика возраста outbox показывает задержку. Сбой между подтверждением и пометкой допускает повторную доставку с тем же ID. Подтверждённые записи хранятся 30 дней, незавершённые — до подтверждения.

## Потребители и очереди ошибок

Потребитель подтверждает сообщение после завершения обработки. Worker использует `eventId` как уникальный ключ inbox. Notifications сохраняет запись по уникальному `notificationId`. SignalR-вызов может повториться, фронтенд объединяет уведомления по ID.

Для временных ошибок предусмотрены три повтора с интервалами 1, 5 и 15 секунд. Ошибки схемы и несовместимые поля сразу направляются в `<queue>_error`. Там же хранятся сообщения с исчерпанными попытками. Сообщения без подходящего обработчика попадают в `<queue>_skipped`. Обе очереди контролируются в Prometheus, `_error` используется как DLQ приложения.

Для ручного восстановления найти запись по `eventId`, исправить данные или обработчик, отправить корректное сообщение в исходный endpoint и проверить результат перед удалением копии из DLQ. Повтор того же логического события сохраняет ID.

## Карта файлов и владельцы изменений

| Файл / каталог | Что уже есть или что нужно сделать |
|---|---|
| `Auth/Directory.Build.props` | Уже фиксирует `MassTransitVersion=8.5.12`; сохранить единым для всех проектов |
| `Auth/HabitTracker.Contracts/Events.cs` | Уже содержит три события и IVersionedEvent; не заводить дубликаты в сервисах |
| `Auth/HabitTracker.Messaging/RabbitMqSettings.cs` | Уже задаёт exchange names, fanout, durable и publisher confirmations |
| `Auth/HabitTracker.Messaging/MongoOutbox.cs`, `OutboxEvent.cs`, `OutboxPublisher.cs`, `EventContext.cs` | Готовая публикация; новый Worker подключает к своей базе |
| `deploy/rabbitmq/definitions.json` | Уже содержит exchanges, две очереди и пять bindings; credentials не хранить здесь |
| `Auth/HabitTracker.Worker/Program.cs`, `Consumers/` | Владелец Worker подключает два consumer к одному endpoint |
| `Auth/HabitTracker.Notifications/Program.cs`, `Consumers/` | Владелец Notifications подключает один consumer |
| `docs/rabbitmq-operations.md` | Создать инструкцию диагностики, bootstrap, безопасного replay |

Транспортный этап не реализует бизнес-логику consumer за владельцев сервисов. Его результат — единые настройки, декларация топологии, проверка регистрации и интеграционные тесты. Если исходники недоступны, предоставить точные блоки DI для встраивания и контрактные тесты, не переписывая неизвестный Program.cs.

## Регистрация транспорта без неявных очередей

1. Считать секцию `RabbitMq`, валидировать host/vhost/credentials. В сервисе-потребителе отсутствие брокерной конфигурации — ошибка запуска, а не режим молчаливого пропуска.
2. В `AddMassTransit` зарегистрировать классы consumer. В `UsingRabbitMq` вызвать существующий `settings.Configure(bus)`.
3. Объявить нужный `ReceiveEndpoint` с фиксированным именем из таблицы. Внутри — retry, явные bindings либо стандартная consume topology с уже заданными именами контрактов и `ConfigureConsumer` для всех типов этого endpoint.
4. Не вызывать дополнительный `ConfigureEndpoints`, который может создать очереди для тех же consumer повторно. Если используется общая автоматическая настройка, доказать тестом, что реально существуют только предусмотренные бизнес-очереди.
5. Получатель возвращает успех только после завершения транзакции/сохранения и необходимого Hub-вызова. Исключение временной ошибки должно выйти в pipeline MassTransit; перехватывание с одним log и return приводит к ошибочному ACK.

Предлагается в каждом сервисе собственное `InvalidEventException` для некорректной нагрузки с исключением из retry. Бизнес-дубликат считается успешной обработкой, а не ошибкой. Не делать `DiscardFaultedMessages` или `DiscardSkippedMessages`: сообщения должны оставаться диагностируемыми.

Индексы и транзакции обеспечивают логическую идемпотентность, а не RabbitMQ. Не полагаться на порядок сообщений при параллельных потребителях. `revision` обязателен для снимков даже при FIFO одного канала.

## Bootstrap и совместимость определений

Bootstrap создаёт vhost, три contract exchange, два endpoint exchange, две очереди и пять связей из README. Проверяет durable/fanout/autoDelete, затем наличие каждого binding. Наличие только RabbitMQ TCP-порта не означает готовность приложения.

Повторный импорт совместимых определений безопасен. При обнаружении несовместимого типа/параметров существующей очереди завершить проверку ошибкой и сохранить данные для решения оператором; не удалять очередь автоматически. Ошибочные `_error`/`_skipped` возникают по мере доставки; проверка базовой топологии не обязана требовать их до первого такого сообщения.

Издатель WebAPI допускает простой RabbitMQ после настройки: сохраняет outbox и догоняет. Начальное создание durable очередей выполняется до первой публикации. Тест с отсутствующим consumer выполняется с **существующей очередью**, а не с отсутствующей топологией.

## План проверки транспорта

- [ ] **Контракт:** JSON roundtrip каждого v1 сохраняет UUID, long revision, UTC и nullable. В брокере MassTransit envelope имеет правильный message type; MessageId/CorrelationId совпадают с полями, trace headers переданы.
- [ ] **Топология:** чистый bootstrap, повторный bootstrap и несовместимые параметры. Сохранённый пример вывода подтверждает две очереди и все пять связей.
- [ ] **Маршрутизация:** HabitChangedV1 и SummarySettingsChangedV1 поступают только в Worker endpoint; NotificationReadyV1 — в Notifications endpoint. Нет конкурирующих автоматически созданных очередей тех же consumer.
- [ ] **Простой:** consumer остановлен, publisher подтверждает запись; сообщение остаётся ready и обрабатывается после старта. Брокер остановлен — outbox остаётся pending/publishing до восстановления lease.
- [ ] **Повтор:** повторить доставку одного eventId; Worker применяет одну операцию inbox; Notifications хранит один notificationId. Повтор SignalR допустим и проверяется клиентом отдельно.
- [ ] **Ошибка:** временный сбой повторяется 1/5/15 секунд, затем error; malformed payload сразу error; валидный envelope без подходящего consumer — skipped. Проверять именно очередь endpoint, а не произвольную одноимённую тестовую коллекцию.
- [ ] **Восстановление:** replay после исправления даёт один логический результат; ID сохранены. В отчёте указать число физических доставок и логических результатов отдельно.

Тестовые издатели использовать через MassTransit, а считывание envelope допустимо через RabbitMQ Management для диагностики. Не составлять production envelope вручную без проверки type URN и headers. Для тестов выбирать изолированный broker/vhost и отделять их от теста WebAPI, блокирующего подключения.

## Процедура ручного replay

1. Зафиксировать source queue, eventId, notificationId (если есть), тип и текст ошибки без секретов. Проверить, не существует ли уже успешный inbox/notification.
2. Исправить причину в обработчике или корректную нагрузку, сохранив ID логического события. Не подменять владельца уведомления для обхода ошибки.
3. Инструментом replay прочитать исходный envelope и переотправить в **исходный receive endpoint** с сохранёнными метаданными. Публикация в exchange контракта может разослать другим подписчикам; для восстановления одного consumer нужен адресный replay.
4. Дождаться подтверждённого результата и отсутствия нового fault. Только затем удалить конкретную обработанную копию из DLQ, не purge всей очереди.

Для будущего инструмента создать, например, `scripts/replay-message` с обязательными явными source/destination/eventId и dry-run. Реальные секреты вводятся через env. Массовый replay и очистка рабочих очередей не входят в автоматический bootstrap.

**Готовность:** регистрация и топология совместимы со всеми тремя сервисами, тесты доставки/повторов/error/skipped пройдены, инструкция воспроизводима. До появления consumers проверка одной публикации не закрывает весь транспортный этап.
