# Развёртывание в Яндекс Облаке

**Статус.** Полный локальный стенд спринта 3 описан в [README](../README.md). Этот план фиксирует дальнейшую облачную выкладку и подготовку производственной конфигурации.

**Цель** — развернуть публичный учебный стенд на одной VM в Яндекс Облаке с k3s. Результат этого этапа включает облачные манифесты, запуск по HTTPS и инструкции по обновлению, откату и восстановлению.

**Основа** — [архитектура](README.md), [топология RabbitMQ](04-rabbitmq.md), [телеметрия](06-observability.md), [CI/CD](07-ci-cd-and-acceptance.md).

## Готовые компоненты

- Dockerfile для WebAPI, Worker, Notifications и фронтенда уже подключены в [`compose.yaml`](../compose.yaml). Этот файл задаёт контексты сборки, постоянные тома, MongoDB replica set, RabbitMQ и проверки готовности. Локальные команды запуска и остановки собраны в [README](../README.md).
- RabbitMQ использует готовые [`definitions.json`](../deploy/rabbitmq/definitions.json) и [`bootstrap.sh`](../deploy/rabbitmq/bootstrap.sh). При подготовке Kubernetes Job переиспользовать ту же декларацию и проверку топологии. Изолированные интеграционные тесты запускаются через [`compose.webapi-tests.yaml`](../compose.webapi-tests.yaml).
- В `deploy/k8s` есть [Deployment и Service для Notifications](../deploy/k8s/notifications.yaml) и [шаблон Ingress](../deploy/k8s/habit-tracker-ingress.yaml). Их нужно довести до облачной конфигурации с опубликованным образом, секретами и реальным доменом.

## Kubernetes в Яндекс Облаке

- [ ] Создать Linux VM с постоянным публичным IP, DNS-записью и диском для данных. Установить k3s, включить шифрование Secrets на диске и использовать штатные Traefik, ServiceLB и `local-path`. Открыть извне `80/443`, SSH ограничить адресами администраторов и согласованным маршрутом для выкладки. `kubectl` выполняется на VM. [Компоненты k3s](https://docs.k3s.io/installation/packaged-components)
- [ ] Создать Yandex Container Registry. Разделить сервисные аккаунты публикации и скачивания образов. Хранить ключ публикации в GitHub Environment, ключ скачивания — в `imagePullSecret` кластера. Публиковать четыре образа `webapi`, `worker`, `notifications`, `frontend` с тегом коммита и разворачивать по digest. [Доступ к приватным образам](https://yandex.cloud/en/docs/managed-kubernetes/operations/private-registries)
- [ ] Подготовить `deploy/k8s` с namespace `habit-tracker`, ConfigMap, шаблонами ссылок на Secret, Deployments, StatefulSets, Services, PVC, probes и NetworkPolicy. MongoDB и RabbitMQ используют по одной реплике и постоянному тому. Инициализацию БД и топологии выполнить отдельным Job, дождаться успеха до старта приложений. Backend, БД, брокер и телеметрия используют ClusterIP. В стартовой конфигурации — одна реплика каждого backend, для проверки lease Worker масштабируется до двух. Notifications использует `Recreate`.
- [ ] Настроить аутентификацию MongoDB в облачной конфигурации. Для replica set смонтировать keyfile с подходящими правами. Создать для WebAPI, Worker и Notifications учётные записи с доступом только к своим базам. URI включают `replicaSet=rs0` и нужный `authSource`. Если переносятся существующие данные, сохранить имя базы WebAPI и коллекции пользователей.
- [ ] Настроить HTTPS на Traefik с cert-manager и сертификатом Let's Encrypt. Маршруты `/api/notifications` и `/hubs/notifications` идут в Notifications, остальные `/api` — в WebAPI, `/` — во frontend. Проверить маршрутизацию более специфичных путей и WebSocket Upgrade. Закрыть внутренние маршруты, RabbitMQ Management, БД и панели телеметрии от внешнего доступа. Для администрирования использовать `kubectl port-forward`.
- [ ] TLS завершается на ingress. Внутри кластера сервисы используют HTTP. Настроить forwarded headers только от доверенного прокси и внешнее перенаправление на HTTPS. Ограничить доступ к внутреннему API Worker сетевой политикой и ключом.
- [ ] Передавать `ASPNETCORE_HTTP_PORTS`, `MongoDbSettings__ConnectionString`, `MongoDbSettings__DatabaseName`, `MongoDbSettings__UsersCollectionName`, `JwtSettings__Issuer`, `JwtSettings__Audience`, `JwtSettings__Secret`, `WorkerApi__BaseUrl`, `WorkerApi__SharedKey`, `RabbitMq__*`, `OTEL_EXPORTER_OTLP_ENDPOINT` и `Features__NotificationsEnabled` через env. Секреты подключать только к сервисам, которые их используют. Один и тот же JWT-ключ нужен WebAPI и Notifications. `Features__NotificationsEnabled=false` на WebAPI скрывает новые операции настройки и отключает запись новых событий. Существующие HTTP-операции продолжаются, накопленный outbox сохраняется.

## Обновление, сохранность и приёмка

- [ ] Зафиксировать CPU, память и объёмы хранения для каждого контейнера после пробного запуска полного стека на VM. Проверить, что ELK и MongoDB укладываются в выделенные ресурсы при одновременной работе.
- [ ] Перед первым обновлением сделать согласованную резервную копию MongoDB и сохранить определения RabbitMQ. Обновить сначала топологию и совместимых потребителей, затем WebAPI и фронтенд. При переносе существующей БД восстановить данные до включения обработки и проверить вход старого аккаунта.
- [ ] Проверить `kubectl rollout status` для всех приложений, успешный HTTPS и сценарии из [приёмки](07-ci-cd-and-acceptance.md). При ошибке развернуть предыдущий набор digest с сохранением PVC. Для возврата к образам до спринта 3 сначала выполнить [миграцию отката](#откат-на-версию-до-спринта-3).
- [ ] Описать резервное копирование вне VM и тест восстановления MongoDB. Проверить сохранность очередей при обычном перезапуске RabbitMQ. Потеря диска RabbitMQ требует восстановления топологии и выборочной повторной публикации из сохранённых outbox. Документировать окно хранения и ограничения восстановления данных одного узла.

## Что добавить для облака

| Артефакт | Содержание |
|---|---|
| `deploy/mongo/` | Скрипты облачной инициализации replica set, пользователей и прав |
| `deploy/k8s/` | Дополнить имеющиеся манифесты недостающими ресурсами и настройками облака |
| `deploy/observability/` | Подключение телеметрии по [отдельному плану](06-observability.md) |
| `docs/deployment.md`, `docs/recovery.md` | Инструкции по выкладке, резервному копированию и восстановлению |

Структуру манифестов можно выбрать обычными YAML или Kustomize; для первого этапа достаточно YAML + overlays для отличий среды. Не вводить Helm/operator/service mesh только ради этого плана. Версии образов закрепить; production образы приложений задавать digest из CI.

### Маршрутизация и HTTP

| Путь снаружи | Upstream | Особенности |
|---|---|---|
| `/api/notifications` и вложенные пути | Notifications:80 | Более специфичное правило раньше общего API |
| `/hubs/notifications` и вложенные транспортные пути | Notifications:80 | negotiate + WebSocket Upgrade, достаточно долгий read timeout |
| Остальные `/api` | WebAPI:80 | JWT Bearer не терять в прокси |
| `/` и клиентские страницы | frontend | SPA fallback |
| `/internal`, `/metrics`, `/health`, панели и management | Нет публичного backend-маршрута | Доступ только внутри сети/через административный туннель |

SPA fallback для неизвестного публичного URL может вернуть HTML; это не означает, что внутренний API опубликован. Проверка изоляции должна смотреть фактический upstream. Прокси не должен логировать query `access_token` Hub. TLS завершается на ingress, backend доверяет forwarded headers только настроенному прокси, чтобы не получить циклы redirect или поддельный scheme.

Backend слушает 80 на всех интерфейсах контейнера. Для non-root обеспечить возможность bind именно этого порта средствами выбранного runtime/securityContext, проверить под реальным UID. Не ограничиваться EXPOSE: он не настраивает Kestrel. Включить tzdata/нужную поддержку IANA зон и проверить календарный smoke в runtime image.

## Чёткие условия для Kubernetes

- Одна VM, k3s, namespace `habit-tracker`; `local-path` PVC сохраняют состояние между перезапусками pod, но не защищают от потери VM/диска. Это учебный стенд, не HA-кластер.
- WebAPI/Worker/Notifications — Deployment; MongoDB/RabbitMQ — StatefulSet с PVC. Notifications replicas=1 и Recreate; Worker допускает scale до 2.
- Дождаться writable primary MongoDB, создания учётных записей и готовности топологии RabbitMQ до старта приложений. Bootstrap Jobs идемпотентны. Worker завершает загрузку расписаний после готовности WebAPI.
- Startup probe даёт время на индексы/загрузку, liveness проверяет только процесс, readiness — зависимости по README. Проба не пишет данные. При длительном bootstrap не убивать исправный Worker liveness-проверкой зависимостей.
- NetworkPolicy разрешает ingress → public backend, Worker → WebAPI internal, приложения → свои зависимости и DNS, monitoring → metrics. Проверить фактическую работу сетевой политики в выбранном k3s, не считать наличие YAML доказательством изоляции.
- Secrets: Mongo service credentials, Rabbit credentials, общий Worker key только WebAPI/Worker, JWT signing key только WebAPI/Notifications, registry pull credentials у workload. Frontend не получает backend secrets через VITE-переменные.
- CPU/memory requests/limits и PVC размер определить пробным запуском всех компонентов, записать измеренные значения в документации. Если ресурсы не известны, оставить именованные параметры для заполнения, не выдавать произвольный размер VM за проверенный.

## Проверка и команды для сдачи

Перед выкладкой проверить конфигурацию и собрать существующие образы из корня репозитория. Команды запуска и остановки локального стенда приведены в [README](../README.md).

```powershell
docker compose config --quiet
docker compose build
```

Не печатать полный интерполированный `docker compose config` в общий лог: он может раскрыть env secrets. Для облака перед применением использовать dry-run и diff в настроенном контексте; настоящая VM/DNS/registry должны быть предоставлены владельцем среды. Подготовить отдельные команды bootstrap и rollout в создаваемой инструкции, включая явную проверку namespace/context.

Матрица приёмки:

| Сценарий | Доказательство |
|---|---|
| Чистый локальный старт | Все зависимости готовы, вход работает, включённое напоминание проходит цепочку |
| Второй старт | Пользователь, привычка, уведомление и очередь сохранены; bootstrap не ломает данные |
| Недоступность брокера | API сохраняет изменение/outbox, после restart доставляет |
| Недоступность Mongo | Readiness 503, liveness остаётся успешным |
| Две реплики Worker | Один логический результат, lease восстановлен после остановки владельца |
| Внешняя сеть | HTTPS и WebSocket работают, internal/management не идут во внутренние upstream |
| Обновление и rollback | Новые digests применились; предыдущий комплект можно вернуть без удаления PVC |
| Восстановление | В отдельной среде восстановлены согласованные данные MongoDB и топология RabbitMQ, проверены вход и история |

### Откат на версию до спринта 3

Старые образы WebAPI и Worker не читают документы с `reminderFirstEligibleDate`. Перед их запуском остановите новые WebAPI и Worker, сохраните резервную копию MongoDB и выполните `mongosh --nodb --file scripts/rollback-reminder-date.js` дважды. Укажите `HABIT_TRACKER_ROLLBACK_MONGO_URI` и `HABIT_TRACKER_ROLLBACK_DATABASE` для соответствующей базы, а в `HABIT_TRACKER_ROLLBACK_ROLE` передайте `webapi` для `habits` и `worker` для `habitSchedules`.

Скрипт удаляет только поле с границей первой отправки из выбранной коллекции. Время напоминания, отметки, история и уже рассчитанные указатели Worker сохраняются. Повторный запуск безопасен. После выполнения для обеих баз можно запускать старые образы с прежними томами.

Резервная копия должна охватывать связанные состояние/инбокс/аутбокс; процедура фиксирует момент и границы согласованности. Published outbox хранится 30 дней — после потери брокерного диска возможен адресный replay сохранившихся событий с прежними ID, но это не обещание восстановления за любой период. Обновлять сначала совместимую топологию и consumers, затем publishers и UI.

**Готовность:** воспроизводимые команды и манифесты, успешная локальная матрица и отдельно подтверждённая облачная проверка. Если доступа к облаку не было, сдать конфигурацию как подготовленную, оставить пункты настоящего rollout/HTTPS/restore открытыми.
