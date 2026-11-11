# Развёртывание — план реализации

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
- [ ] Проверить `kubectl rollout status` для всех приложений, успешный HTTPS и сценарии из [приёмки](07-ci-cd-and-acceptance.md). При ошибке развернуть предыдущий набор digest. Схемы и контракты при первой выкладке добавляются совместимо, PVC сохраняются.
- [ ] Описать резервное копирование вне VM и тест восстановления MongoDB. Проверить сохранность очередей при обычном перезапуске RabbitMQ. Потеря диска RabbitMQ требует восстановления топологии и выборочной повторной публикации из сохранённых outbox. Документировать окно хранения и ограничения восстановления данных одного узла.
