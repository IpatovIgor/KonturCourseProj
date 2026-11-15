# Телеметрия — план реализации

**Статус:** общий стек телеметрии предстоит реализовать. Уже есть корреляция событий, trace headers в outbox и Meter `HabitTracker.Outbox` с `outbox.oldest.unpublished.age`; это не готовые Prometheus/Jaeger/ELK. **Комплект:** этот файл + `plan/README.md`, содержащий границы сервисов, событийные цепочки, DTO и конфигурацию.

**Результат:** единая конфигурация логов/метрик/трасс для трёх backend, конфиги инфраструктуры, запросы диагностики и подтверждённый демонстрационный сценарий. Без исходников сервисов предоставить адаптеры и точные места подключения, не обещать автоматическое инструментирование неизвестных методов.

**Цель** — увидеть состояние трёх сервисов и проследить путь от HTTP-запроса до результата Worker и доставки в Notifications.

**Основа** — [архитектура](README.md), [контракты RabbitMQ](04-rabbitmq.md), [развёртывание](05-deployment.md).

## Общие endpoints

| Endpoint | Условие |
|---|---|
| `GET /health/live` | Процесс способен ответить. Проверка не обращается к MongoDB и RabbitMQ |
| `GET /health/ready` | Инициализация завершена, необходимые зависимости доступны |
| `GET /metrics` | Текстовый формат Prometheus, доступ из сети мониторинга |

WebAPI требует доступной MongoDB и индексов. Простой RabbitMQ отражается в метриках outbox, WebAPI может сохранять операции. Worker требует MongoDB, завершённой загрузки расписаний и соединения с брокером. Notifications требует MongoDB и готового потребителя. Kubernetes использует `startupProbe` и достаточно времени на начальную инициализацию. Пробы возвращают статус без данных конфигурации.

## Шаги

- [ ] Подключить Serilog к каждому сервису. Писать структурированный JSON в stdout с `timestamp`, `level`, `service`, `environment`, `correlationId`, `traceId`, `spanId`, статусом и длительностью операции. Принимать валидный UUID из `X-Correlation-ID` либо генерировать новый, возвращать его в HTTP-ответе. Для периодического задания связывать логи с `jobId` и событием, которое обновило расписание. Исключить токены, секреты, query string Hub, пароли и содержимое пользовательских форм.
- [ ] Настроить Filebeat для контейнерных логов Kubernetes, Logstash для разбора Serilog JSON и передачи в Elasticsearch, Kibana для фильтрации по сервису, уровню и `correlationId`. Сохранять Elasticsearch на PVC. В отчёте приложить конфигурацию маршрута логов и пример поиска. Установить срок хранения логов учебного стенда семь дней.
- [ ] Подключить Prometheus scrape трёх `/metrics` и RabbitMQ Prometheus plugin. Собирать HTTP latency, счётчики успехов и ошибок, длину ready и unacked очередей, размер `_error` и `_skipped`, длительность заданий, повторы, истёкшие lease и возраст outbox. Экспортировать `tasks_pending_total` как gauge числа `pending`, включая задания с отложенным сроком. Использовать labels `service`, `kind`, `result`, шаблон маршрута и класс статуса. В labels не передавать ID пользователей, задач и запросов.
- [ ] Подключить OpenTelemetry для входящего ASP.NET Core, исходящего `HttpClient`, операций MongoDB, MassTransit и явных `Activity` фоновых задач и вызовов SignalR. Передавать W3C trace context через HTTP и заголовки сообщений. Записывать его вместе с outbox. При отложенном выполнении запускать отдельную трассу с `ActivityLink` к сохранённому контексту расписания. Экспортировать OTLP через Collector в Jaeger.
- [ ] Для доставки NotificationReadyV1 создать span сохранения и вызова `NotificationReceived` с безопасными атрибутами `notification.id` и `notification.kind`. Связать вызов SignalR с consumer span. Передать фронтенду `correlationId` и `traceId` как необязательные диагностические поля события, не передавая их в REST-истории. Отобразить сквозную цепочку и отложенную трассу в отчёте.
- [ ] Подготовить запросы PromQL для ошибок HTTP, роста `_error`, задержки заданий и старейшей записи outbox. Принять контрольные условия для демонстрации — очередь ошибок больше нуля и возраст outbox больше пяти минут.

## Проверка

- [ ] После запроса к WebAPI отфильтровать JSON-логи в Kibana по `correlationId` и найти в Jaeger цепочку WebAPI → RabbitMQ → Worker → MongoDB.
- [ ] Для готового уведомления показать связанные span Worker, публикации RabbitMQ, сохранения Notifications и вызова SignalR. Сверить диагностический ID в браузере.
- [ ] Остановить брокер и проверить, что WebAPI сохраняет изменения, а возраст outbox растёт. После восстановления записи публикуются. При остановке MongoDB readiness меняется без падения liveness.
- [ ] Отправить ошибочное сообщение, проверить видимость `_error` в метриках и журнале. Убедиться, что JWT и ключи отсутствуют в stdout, Kibana и Jaeger.

## Файлы и порядок подключения

| Артефакт | Ответственность |
|---|---|
| `Auth/HabitTracker.Observability/` (предлагаемый новый class library net8.0) | Общие DI extensions Serilog/OpenTelemetry, middleware корреляции; без зависимости на WebAPI |
| `Auth/{WebApplication1,HabitTracker.Worker,HabitTracker.Notifications}/Program.cs` | Вызов общего extension с именем сервиса, endpoints/probes |
| Worker scheduler/executor, Notifications consumer | Явные Activity и бизнес-метрики с точками начала/окончания |
| `deploy/observability/filebeat.yml`, `logstash.conf` | Container stdout → parse JSON → Elasticsearch |
| `deploy/observability/prometheus.yml`, `alerts.yml` | Scrape сервисов и Rabbit, контрольные правила |
| `deploy/observability/otel-collector.yaml` | Приём OTLP, экспорт в Jaeger |
| `deploy/observability/` настройки Elasticsearch/Kibana/Jaeger | Хранение, доступ внутри сети и retention |
| `docs/observability.md` | Имена instruments, PromQL, поиски Kibana и примеры trace |
| Тесты общего middleware и инструментирования | Корреляция, отсутствие секретов, связность span, низкая кардинальность |

Новая общая библиотека — предложение для устранения дублирования; она не должна тянуть MongoDB-модели одного сервиса в другой. Версии новых пакетов Serilog/OpenTelemetry/экспортеров выбрать совместимыми с net8.0 и закрепить в проекте после restore/build, не обновляя MassTransit и MongoDB попутно.

## Контракт логирования

Имена `service`: `webapi`, `worker`, `notifications`. Env определяет environment. Формат timestamp — UTC ISO 8601; JSON-объект на одну запись stdout. Операция содержит результат и duration, исключение — безопасное описание и stack trace в серверном журнале, не в HTTP ответе. User input и credentials исключены из структурированных свойств.

На HTTP-входе принять **один** валидный UUID `X-Correlation-ID`, иначе создать новый. Вернуть его в ответе, открыть log scope. Существующий `RequestEventContext` WebAPI уже выбирает корреляцию: при интеграции использовать один и тот же ID в response, логах и событиях, не создавать второй независимый источник. `traceId` принадлежит Activity и не обязан совпадать с correlationId.

На consumer-входе использовать correlationId сообщения; на периодическом задании — сохранённый контекст расписания и jobId. Не писать userId/jobId в labels метрик, но jobId допустим в логах и trace для диагностики. Запретить access_token в логах reverse proxy и auto HTTP instrumentation: фильтра одного ILogger недостаточно.

Проверка: отправить валидный/невалидный/отсутствующий correlation header и убедиться в согласованности ответ → событие → consumer logs. Выполнить запрос с тестовым секретом-маркером в Authorization/Hub query и проверить, что маркер не попал в собранные logs/spans.

## Метрики и ограничения

Таблица ниже задаёт желаемые **Prometheus-имена** новых метрик. Существующий .NET instrument outbox может преобразовываться экспортером; зафиксировать его фактическое имя или настроить явное отображение в `outbox_oldest_unpublished_age_seconds`. Не писать alert на неподтверждённое имя.

| Имя | Тип | Что измеряет |
|---|---|---|
| `tasks_pending_total` | Gauge, несмотря на историческое имя с total | Число всех pending jobs, включая будущий backoff |
| `worker_job_duration_seconds` | Histogram | Длительность попытки исполнения, labels kind/result |
| `worker_job_http_retries_total` | Counter | Временные HTTP-сбои, а не каждый захват lease |
| `worker_job_lease_expired_total` | Counter | Повторный захват истёкшего lease |
| `worker_job_delay_seconds` | Histogram | max(0, фактический старт - dueAtUtc) |
| `outbox_oldest_unpublished_age_seconds` | Gauge | Возраст старейшей pending/publishing записи, 0 если пусто |

HTTP duration/count собирать одним ASP.NET instrumentation, не дублировать собственным middleware без необходимости. Labels: service, kind, result, шаблон route, класс статуса. Не использовать URL с ID, traceId, exception message, email, произвольные причины ошибок. При нескольких Worker gauge числа jobs из общей БД не суммировать по репликам: агрегировать max для одинакового хранилища; counters отдельных попыток можно суммировать.

RabbitMQ: ready, unacked и размер error/skipped по нужным очередям. При необходимости включить per-queue метрики plugin и проверить, что queue label реально существует; ограничения кардинальности задать по vhost/предусмотренным очередям. Пока error queue не создана, отсутствие ряда не должно ошибочно считаться аварией.

Собрать PromQL для: HTTP доли 5xx, p95 latency, error queue >0, возраст outbox >300 секунд, задержка заданий. В `docs/observability.md` вставить реально работающие запросы по выводу `/metrics` установленной версии; не подменять проверку произвольными именами plugin.

## Трассировка отложенной работы

1. HTTP Activity WebAPI → транзакция MongoDB/outbox. Сохранить W3C контекст уже предусмотренным `EventContext`.
2. Outbox publish → RabbitMQ consumer Worker → транзакция обновления расписания. Проверить propagation MassTransit и отсутствие двойных span одного publish из двух конфигураций.
3. По наступлении срока создать **новую** trace с ActivityLink на контекст сохранённого расписания: не держать HTTP span открытым часами. Добавить kind/jobId, проверку API, результат и outbox.
4. Publish NotificationReady → consumer Notifications → MongoDB save → SignalR send. Передавать безопасные traceId/correlationId в SignalR DTO; не менять базовые поля бизнес-контракта и REST историю.

Не делать span длительностью в целый срок lease или lifetime сервиса: span описывает конкретную попытку/операцию. MongoDB instrumentation должна реально поддерживать выбранный driver либо использовать явные Activity вокруг repository операций; не писать в spans содержимое документов и connection string.

Для локальной демонстрации можно настроить 100% sampling с ограниченным потоком; режим и retention фиксировать явно. Логи хранить семь дней. Jaeger/Elasticsearch/Prometheus панели доступны через локальный доступ или port-forward, не открытый ingress.

## Последовательность приёмки

Сначала подключить и проверить stdout/correlation одного сервиса, затем общую конфигурацию всех трёх; после этого поднять профиль observability и проверить фактическое поступление данных. Затем метрики и alerts, наконец полный tracing сценарий.

| Действие | Что увидеть |
|---|---|
| Изменить напоминание через API | Один correlationId в ответе и логах WebAPI/Worker, трасса транспорта в Jaeger |
| Дождаться срока | Отдельная trace с link, span HTTP eligibility, job result, outbox publish |
| Получить сообщение в браузере | Span сохранения до span SignalR, диагностический traceId соответствует событию |
| Остановить RabbitMQ | Возраст outbox растёт, alert после порога; после запуска возраст возвращается к 0 |
| Имитировать poison message | Error queue видна в Prometheus и логах без раскрытия payload secrets |
| Остановить MongoDB | Ready 503, live успешен; ошибка зависимости видна в метриках |

Запуск после добавления полного Compose: `docker compose --profile observability up -d --wait`. Проверить конфиги штатными валидаторами выбранных образов (например, promtool для Prometheus), затем query API/интерфейсы реально поднятых сервисов. Для корреляции/редактирования секретов использовать автоматические тесты, для вывода в ELK/Jaeger приложить реальные примеры.

**Готовность:** данные действительно поступают по всей цепочке, метрики совпадают с запросами, нет секретов, зафиксированы конфиги и воспроизводимые действия. Если сервисы Worker/Notifications ещё не интегрированы, их части оставлять непроверенными, даже если общий extension компилируется.
