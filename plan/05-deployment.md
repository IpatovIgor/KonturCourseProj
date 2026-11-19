# Развёртывание — план реализации

**Статус.** Полный локальный стенд спринта 3 описан в [README](../README.md). Этот план фиксирует дальнейшую облачную выкладку и подготовку производственной конфигурации.

**Результат:** Dockerfile четырёх приложений, полный Compose с запуском одной командой, k3s-манифесты и инструкции bootstrap/обновления/восстановления. Манифесты не заменяют реализацию сервисов: если соответствующего проекта ещё нет, сборку и rollout пометить ожидающими интеграции. Настройка облачного аккаунта, DNS и секретов требует реальных значений команды; не придумывать их.

**Цель** — обеспечить воспроизводимый локальный запуск и публичный учебный стенд на одной VM в Яндекс Облаке с k3s.

**Основа** — [архитектура](README.md), [топология RabbitMQ](04-rabbitmq.md), [телеметрия](06-observability.md), [CI/CD](07-ci-cd-and-acceptance.md).

## Сборка и локальный запуск

- [ ] Добавить Dockerfile для WebAPI, Worker и Notifications с отдельными стадиями `dotnet restore`, `dotnet publish` и runtime. Dockerfile фронтенда выполняет `npm ci`, `npm run build` и обслуживает `dist` через Nginx с fallback для React. Контейнеры backend слушают `0.0.0.0:80`. Запускать их от непривилегированного пользователя с настройкой, разрешающей bind порта 80.
- [ ] В корне добавить `compose.yaml`, `.env.example` с именами переменных и скрипты `scripts/dev-up.ps1`, `scripts/dev-down.ps1`. Поднять MongoDB replica set, RabbitMQ, три сервиса и React. Включать ELK, Prometheus и Jaeger профилем `observability`. Приложение доступно по `http://localhost:8080`. Проксировать REST и WebSocket с того же origin.
- [ ] Подготовить идемпотентную инициализацию MongoDB. Один узел `rs0`, адрес `mongo:27017` доступен приложениям. Включить auth и keyfile для replica set. Создать отдельные учётные записи для WebAPI, Worker и Notifications с доступом только к своей базе. Сохранить существующие имя базы WebAPI и имя коллекции пользователей в конфигурации миграции. Подготовить virtual host RabbitMQ и bindings из [топологии](04-rabbitmq.md) до запуска издателей.
- [ ] Для Compose, CI и k3s хранить декларацию топологии RabbitMQ в репозитории без учётных данных. Bootstrap проверяет наличие обеих очередей и всех bindings. Несовместимая топология останавливает запуск издателей. В корневом README описать exchange, очереди и порядок проверки.
- [ ] Описать запуск и остановку в корневом README. Для интеграционных тестов поднимать отдельную инфраструктуру и временные базы. Состояние сохраняется между обычными запусками Compose.

## Kubernetes в Яндекс Облаке

- [ ] Создать Linux VM с постоянным публичным IP, DNS-записью и диском для данных. Установить k3s, включить шифрование Secrets на диске и использовать штатные Traefik, ServiceLB и `local-path`. Открыть извне `80/443`, SSH ограничить адресами администраторов. Kubernetes API доступен локальному runner. [Компоненты k3s](https://docs.k3s.io/installation/packaged-components)
- [ ] Создать Yandex Container Registry. Разделить сервисные аккаунты публикации и скачивания образов. Хранить ключ публикации в GitHub Environment, ключ скачивания — в `imagePullSecret` кластера. Публиковать четыре образа `webapi`, `worker`, `notifications`, `frontend` с тегом коммита и разворачивать по digest. [Доступ к приватным образам](https://yandex.cloud/en/docs/managed-kubernetes/operations/private-registries)
- [ ] Подготовить `deploy/k8s` с namespace `habit-tracker`, ConfigMap, шаблонами ссылок на Secret, Deployments, StatefulSets, Services, PVC, probes и NetworkPolicy. MongoDB и RabbitMQ используют по одной реплике и постоянному тому. Инициализацию БД и топологии выполнить отдельным Job, дождаться успеха до старта приложений. Backend, БД, брокер и телеметрия используют ClusterIP. В стартовой конфигурации — одна реплика каждого backend, для проверки lease Worker масштабируется до двух. Notifications использует `Recreate`.
- [ ] Настроить HTTPS на Traefik с cert-manager и сертификатом Let's Encrypt. Маршруты `/api/notifications` и `/hubs/notifications` идут в Notifications, остальные `/api` — в WebAPI, `/` — во frontend. Проверить маршрутизацию более специфичных путей и WebSocket Upgrade. Закрыть внутренние маршруты, RabbitMQ Management, БД и панели телеметрии от внешнего доступа. Для администрирования использовать `kubectl port-forward`.
- [ ] TLS завершается на ingress. Внутри кластера сервисы используют HTTP. Настроить forwarded headers только от доверенного прокси и внешнее перенаправление на HTTPS. Ограничить доступ к внутреннему API Worker сетевой политикой и ключом.
- [ ] Передавать `ASPNETCORE_HTTP_PORTS`, `MongoDbSettings__ConnectionString`, `MongoDbSettings__DatabaseName`, `MongoDbSettings__UsersCollectionName`, `JwtSettings__Issuer`, `JwtSettings__Audience`, `JwtSettings__Secret`, `WorkerApi__BaseUrl`, `WorkerApi__SharedKey`, `RabbitMq__*`, `OTEL_EXPORTER_OTLP_ENDPOINT` и `Features__NotificationsEnabled` через env. Секреты подключать только к сервисам, которые их используют. Один и тот же JWT-ключ нужен WebAPI и Notifications. `Features__NotificationsEnabled=false` на WebAPI скрывает новые операции настройки и отключает запись новых событий. Существующие HTTP-операции продолжаются, накопленный outbox сохраняется.

## Обновление, сохранность и приёмка

- [ ] Зафиксировать CPU, память и объёмы хранения для каждого контейнера после пробного запуска полного стека на VM. Проверить, что ELK и MongoDB укладываются в выделенные ресурсы при одновременной работе.
- [ ] Перед первым обновлением сделать согласованную резервную копию MongoDB и сохранить определения RabbitMQ. Обновить сначала топологию и совместимых потребителей, затем WebAPI и фронтенд. При переносе существующей БД восстановить данные до включения обработки и проверить вход старого аккаунта.
- [ ] Проверить `kubectl rollout status` для всех приложений, успешный HTTPS и сценарии из [приёмки](07-ci-cd-and-acceptance.md). При ошибке развернуть предыдущий набор digest с сохранением PVC. Для возврата к образам до спринта 3 сначала выполнить [миграцию отката](#откат-на-версию-до-спринта-3).
- [ ] Описать резервное копирование вне VM и тест восстановления MongoDB. Проверить сохранность очередей при обычном перезапуске RabbitMQ. Потеря диска RabbitMQ требует восстановления топологии и выборочной повторной публикации из сохранённых outbox. Документировать окно хранения и ограничения восстановления данных одного узла.

## Что создать и как разделить конфигурацию

| Артефакт | Содержание |
|---|---|
| `Auth/{WebApplication1,HabitTracker.Worker,HabitTracker.Notifications}/Dockerfile` | Отдельный multi-stage образ; context `Auth`, включая Directory.Build.props и общие библиотеки |
| `Auth/.dockerignore` | Исключить bin/obj, локальные секреты и результаты тестов, сохранить csproj и общие зависимости |
| `habit-frontend/Dockerfile`, `.dockerignore`, `nginx.conf` | npm ci/build, статический runtime, same-origin REST и WebSocket proxy |
| `compose.yaml`, `.env.example` | Постоянные volumes, зависимости запуска, health, без реальных секретов |
| `scripts/dev-up.ps1`, `scripts/dev-down.ps1` | Проверка обязательных env, запуск/остановка без удаления пользовательских данных |
| `deploy/mongo/` | Идемпотентные скрипты replica set, пользователей и прав; keyfile монтируется отдельно |
| `deploy/rabbitmq/` | Сохранить definitions.json; добавить bootstrap/проверку readiness топологии |
| `deploy/k8s/` | Namespace, config, deployments/statefulsets, services, PVC, bootstrap Jobs, ingress, policies |
| `deploy/observability/` | Точки подключения конфигурации этапа телеметрии; профиль observability |
| `README.md`, `docs/deployment.md`, `docs/recovery.md` | Команды запуска, схема ресурсов, выпуск версии, backup/restore |

Структуру манифестов можно выбрать обычными YAML или Kustomize; для первого этапа достаточно YAML + overlays для отличий среды. Не вводить Helm/operator/service mesh только ради этого плана. Версии образов закрепить; production образы приложений задавать digest из CI.

### Минимальный состав полного Compose

Сервисы: `mongo`, Mongo bootstrap, `rabbitmq`, Rabbit bootstrap, `webapi`, `worker`, `notifications`, `frontend`. Три базы — `habit_tracker_auth`, `habit_tracker_worker`, `habit_tracker_notifications` (первое имя сверить с существующей конфигурацией). Каждому приложению отдельный Mongo user с readWrite только своей базы; root используется только bootstrap/оператором. URI включают authSource правильной БД и replicaSet=rs0. Адрес участника replica set должен разрешаться внутри сети приложений, например `mongo:27017`.

Порядок готовности: Mongo writable primary → пользователи/права; Rabbit vhost/очереди/bindings → приложения; WebAPI готов → Worker завершает bootstrap → Worker ready. Notifications зависит от Mongo и Rabbit, frontend proxy может стартовать раньше backend, но общий smoke ждёт их готовности. Не создавать циклическую зависимость «WebAPI ждёт Worker, Worker ждёт WebAPI».

Mongo keyfile должен иметь допустимые права и владельца; проверить это в Linux-контейнере, особенно если запуск с Windows. Секреты из локальных файлов/Compose secrets не копировать в image и git. Bootstrap после обычного restart не пересоздаёт базы и не сбрасывает пароли.

`dev-down.ps1` выполняет обычный down без `-v`. Удаление volumes — отдельная явно разрушительная операция, не поведение остановки. Тестовый Compose остаётся отдельным и может удалять только свои временные volumes. Порты Mongo/Rabbit Management не публиковать на все интерфейсы хоста; доступ администратора через localhost/port-forward.

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
- Bootstrap Jobs идемпотентны и имеют явный признак успеха; workflow ждёт завершения прежде приложения. `kubectl apply` сам не гарантирует необходимый порядок готовности.
- Startup probe даёт время на индексы/загрузку, liveness проверяет только процесс, readiness — зависимости по README. Проба не пишет данные. При длительном bootstrap не убивать исправный Worker liveness-проверкой зависимостей.
- NetworkPolicy разрешает ingress → public backend, Worker → WebAPI internal, приложения → свои зависимости и DNS, monitoring → metrics. Проверить фактическую работу сетевой политики в выбранном k3s, не считать наличие YAML доказательством изоляции.
- Secrets: Mongo service credentials, Rabbit credentials, общий Worker key только WebAPI/Worker, JWT signing key только WebAPI/Notifications, registry pull credentials у workload. Frontend не получает backend secrets через VITE-переменные.
- CPU/memory requests/limits и PVC размер определить пробным запуском всех компонентов, записать измеренные значения в документации. Если ресурсы не известны, оставить именованные параметры для заполнения, не выдавать произвольный размер VM за проверенный.

## Проверка и команды для сдачи

После создания Dockerfile и Compose, из корня:

```powershell
docker build -f Auth/WebApplication1/Dockerfile -t habit-webapi:local Auth
docker build -f Auth/HabitTracker.Worker/Dockerfile -t habit-worker:local Auth
docker build -f Auth/HabitTracker.Notifications/Dockerfile -t habit-notifications:local Auth
docker build -t habit-frontend:local habit-frontend
docker compose config --quiet
./scripts/dev-up.ps1
# Проверить приложение на http://localhost:8080
./scripts/dev-down.ps1
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

### Откат на версию до спринта 3

Старые образы WebAPI и Worker не читают документы с `reminderFirstEligibleDate`. Перед их запуском остановите новые WebAPI и Worker, сохраните резервную копию MongoDB и выполните `mongosh --nodb --file scripts/rollback-reminder-date.js` дважды. Укажите `HABIT_TRACKER_ROLLBACK_MONGO_URI` и `HABIT_TRACKER_ROLLBACK_DATABASE` для соответствующей базы, а в `HABIT_TRACKER_ROLLBACK_ROLE` передайте `webapi` для `habits` и `worker` для `habitSchedules`.

Скрипт удаляет только поле с границей первой отправки из выбранной коллекции. Время напоминания, отметки, история и уже рассчитанные указатели Worker сохраняются. Повторный запуск безопасен. После выполнения для обеих баз можно запускать старые образы с прежними томами.
| Restore | В отдельной среде восстановлены согласованные Mongo данные и topology, проверены вход/история |

Резервная копия должна охватывать связанные состояние/инбокс/аутбокс; процедура фиксирует момент и границы согласованности. Published outbox хранится 30 дней — после потери брокерного диска возможен адресный replay сохранившихся событий с прежними ID, но это не обещание восстановления за любой период. Обновлять сначала совместимую топологию и consumers, затем publishers и UI.

**Готовность:** воспроизводимые команды и манифесты, успешная локальная матрица и отдельно подтверждённая облачная проверка. Если доступа к облаку не было, сдать конфигурацию как подготовленную, оставить пункты настоящего rollout/HTTPS/restore открытыми.
