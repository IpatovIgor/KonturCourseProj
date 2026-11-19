# Проверки WebAPI

Из корня репозитория запустите изолированные MongoDB replica set и RabbitMQ через Docker Compose. Тесты создают и удаляют собственные базы. Проверка готовности RabbitMQ запускает импорт [топологии событий v1](../deploy/rabbitmq/definitions.json), если связи ещё отсутствуют. Compose ждёт появления всех пяти связей. Очередь `worker-habit-events-v1` получает события привычек и настроек сводки. Очередь `notifications-ready-v1` принимает готовые уведомления Worker.

```powershell
docker compose -f compose.webapi-tests.yaml -p kontur-webapi-tests up -d --wait --wait-timeout 240

$env:KONTUR_TEST_MONGO_URI = 'mongodb://127.0.0.1:27142/?directConnection=true&replicaSet=rs0'
$env:KONTUR_TEST_RABBIT_URI = 'rabbitmq://127.0.0.1:5673'
$env:KONTUR_TEST_RABBIT_MANAGEMENT_URI = 'http://127.0.0.1:15673'
$env:KONTUR_TEST_RABBIT_USER = 'webapi-test'
$env:KONTUR_TEST_RABBIT_PASSWORD = 'webapi-test-only'

dotnet test Auth/WebApplication1.sln -m:1

docker compose -f compose.webapi-tests.yaml -p kontur-webapi-tests down -v
```

RabbitMQ-тест временно запрещает AMQP-подключения к `habit-tracker` и считывает сообщения из тестовой очереди. Используйте для него отдельный экземпляр брокера, как в Compose выше. При запуске без переменных `KONTUR_TEST_*` интеграционные тесты пропускаются.

После развёртывания ingress проверьте снаружи запрос `GET /internal/v1/habit-schedules`. Ответ должен прийти от публичного прокси или фронтенда. Внутри сети тот же запрос к WebAPI с `X-Worker-Key` должен вернуть JSON-страницу, а без заголовка — `401`. Сверьте журнал маршрутизации ingress, чтобы подтвердить отсутствие запроса к upstream WebAPI со стороны публичного адреса.

[Манифест Ingress](../deploy/k8s/habit-tracker-ingress.yaml) направляет в WebAPI только публичный префикс `/api`. Подставьте домен и TLS Secret стенда перед проверкой.
