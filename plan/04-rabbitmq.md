# RabbitMQ и MassTransit — общая спецификация

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
