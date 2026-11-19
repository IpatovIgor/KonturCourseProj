# Эксплуатация RabbitMQ

## Подготовка брокера

До первого запуска WebAPI и Worker импортируйте [definitions.json](../deploy/rabbitmq/definitions.json). Учётные записи и права на vhost `habit-tracker` задаются при развёртывании. JSON содержит только топологию. Скопируйте определения и [bootstrap.sh](../deploy/rabbitmq/bootstrap.sh) в `/etc/rabbitmq` контейнера брокера и выполните `sh /etc/rabbitmq/bootstrap.sh`. В тестовом Compose команда служит healthcheck.

Скрипт проверяет типы, флаги и аргументы двух classic-очередей и пяти fanout exchange, а также пять связей. Для бизнес-очередей ожидается пустая эффективная политика. Настройки RabbitMQ policy и operator policy, включая ограничение длины или срока хранения, считаются несовместимыми. Если топология неполная, скрипт импортирует определения и проверяет её повторно. Код 2 означает несовместимые параметры, код 3 — сбой команды RabbitMQ. При несовместимости сохраните данные и согласуйте исправление с оператором брокера. Запускайте издателей после кода 0.

Команды диагностики выполняются внутри контейнера брокера.

```sh
rabbitmqctl -q -p habit-tracker list_queues name type durable auto_delete effective_policy_definition messages_ready messages_unacknowledged
rabbitmqctl -q -p habit-tracker list_exchanges name type durable auto_delete
rabbitmqctl -q -p habit-tracker list_bindings source_name destination_name destination_kind
```

Ниже приведены пять пользовательских связей после импорта и повторного запуска.

```text
habit-changed-v1              worker-habit-events-v1  exchange
summary-settings-changed-v1   worker-habit-events-v1  exchange
notification-ready-v1         notifications-ready-v1 exchange
worker-habit-events-v1         worker-habit-events-v1  queue
notifications-ready-v1        notifications-ready-v1 queue
```

Если бизнес-очередь растёт, проверьте подключение и readiness её потребителя. Если она пуста, проверьте состояние outbox издателя и привязки. Записи в `_error` указывают на сбой обработки или ошибку контракта. `_skipped` означает, что для полученного типа в endpoint отсутствует обработчик. Эти очереди появляются при первой такой доставке.

## Восстановление отдельного события

1. В RabbitMQ Management найдите исходную очередь, `MessageId` и тип сообщения. Для просмотра используйте режим с возвратом сообщения в очередь. Сопоставьте `eventId` и, для Notifications, `notificationId` с логами и MongoDB. Проверьте inbox или историю до повтора.
2. Устраните причину сбоя. Для исправления полезной нагрузки передайте через `--message` JSON-файл с полями самого события. На верхнем уровне файла должны быть `eventId`, `correlationId`, `userId` и остальные поля контракта. Сохраните `eventId`, `correlationId`, `notificationId` и `userId` исходного события. Если исходный envelope повреждён или его `eventId` расходится с AMQP `MessageId`, инструмент остановится и вернёт доставку в DLQ.
3. Задайте в окружении `KONTUR_REPLAY_AMQP_URI` с адресом брокера, например `amqps://rabbitmq.example.invalid:5671`, и `RabbitMq__VirtualHost`, `RabbitMq__Username`, `RabbitMq__Password`. Используйте учётную запись с правом читать указанную DLQ и публиковать через `amq.default` в этом vhost. [Права доступа RabbitMQ](https://www.rabbitmq.com/docs/access-control)

   ```powershell
   dotnet run --project scripts/replay-message -- --source notifications-ready-v1_error --destination notifications-ready-v1 --event-id '<eventId>' --dry-run
   dotnet run --project scripts/replay-message -- --source notifications-ready-v1_error --destination notifications-ready-v1 --event-id '<eventId>' --publish
   ```

4. Дождитесь подтверждения публикации от брокера, затем проверьте обработку в сервисе и отсутствие нового fault. В том же терминале введите UUID события, чтобы подтвердить удаление выбранной копии из DLQ. Enter, прерванное соединение или запуск с перенаправленным вводом возвращают исходную копию в очередь. Инструмент удерживает прочитанные записи до закрытия канала. Для поиска предусмотрен `--limit` от 1 до 1000, по умолчанию 100. Выполняйте восстановление для одной очереди из одного сеанса.
5. Зафиксируйте отдельно физические доставки и логический результат. Например, первая доставка и один replay дают две доставки, а inbox Worker или история Notifications должны содержать одну запись.

Replay отправляет исходный MassTransit envelope через default exchange с точным именем очереди в routing key. Публикация сохраняет AMQP properties и заголовки, требует publisher confirm и завершается ошибкой при отсутствии очереди. Сбой между публикацией и ACK оставляет возможность повторить операцию с тем же ID. [Маршрутизация через default exchange](https://www.rabbitmq.com/docs/exchanges#default-exchange)

## Мониторинг очередей

```powershell
docker compose -f compose.webapi-tests.yaml --profile rabbitmq-monitoring up -d --wait
```

Prometheus доступен локально на `http://127.0.0.1:19090`. [Конфигурация](../deploy/rabbitmq/prometheus.yml) запрашивает семейство `queue_coarse_metrics` для vhost `habit-tracker`. Для диагностики используйте `rabbitmq_detailed_queue_messages_ready` и `rabbitmq_detailed_queue_messages_unacked`. [Правило](../deploy/rabbitmq/prometheus-alerts.yml) переходит в firing, если в любой прикладной `_error` или `_skipped` в течение минуты есть сообщения. На стенде направьте scrape на внутренний RabbitMQ с включённым `rabbitmq_prometheus` на порту 15692.
