# Notifications

Сервис принимает `NotificationReadyV1` из RabbitMQ, сохраняет уведомление в отдельной базе и отправляет его через SignalR. Первая запись с этим ID остаётся источником для повторной доставки и восстановления истории. React открывает историю и настройки из рабочего пространства. Настройка напоминания находится на карточке привычки.

## Локальный запуск

Нужны .NET 8, Node.js 24, MongoDB и RabbitMQ с топологией из `deploy/rabbitmq/definitions.json`. Для локального стенда можно воспользоваться тестовым Compose из раздела ниже.

Передайте WebAPI и Notifications одинаковые `JwtSettings__Secret`, `JwtSettings__Issuer` и `JwtSettings__Audience`. Укажите URI MongoDB в `MongoDbSettings__ConnectionString` и задайте Notifications отдельную базу через `MongoDbSettings__DatabaseName` (по умолчанию `habit_tracker_notifications`). Для Notifications также обязательны `RabbitMq__Host`, `RabbitMq__Username` и `RabbitMq__Password`. Значение `RabbitMq__VirtualHost` по умолчанию — `habit-tracker`. Пример переменных находится в [корневом .env.example](../.env.example).

В двух терминалах с соответствующим окружением запустите WebAPI и Notifications из корня.

```powershell
dotnet run --project Auth/WebApplication1 --no-launch-profile --urls http://localhost:5160
dotnet run --project Auth/HabitTracker.Notifications --no-launch-profile --urls http://localhost:5162
```

Из `habit-frontend` выполните `npm ci` и `npm run dev`. Vite отправляет `/api/notifications` и `/hubs/notifications` на `NOTIFICATIONS_PROXY_TARGET` (по умолчанию `http://localhost:5162`). Остальные API обслуживает `API_PROXY_TARGET`. Для Hub Vite переносит `access_token` в Bearer-заголовок перед проксированием, чтобы токен не попадал в журналы URL.

## Проверка

Интеграционные тесты используют реальный MongoDB replica set, RabbitMQ и Kestrel с WebSocket. Укажите только изолированную инфраструктуру. Тесты Notifications используют временные базы и читают сообщения из очередей ошибок. Запускайте наборы WebAPI и Notifications последовательно, поскольку тест WebAPI временно блокирует соединения с тестовым vhost.

```powershell
docker compose -f compose.webapi-tests.yaml -p kontur-notifications-tests up -d --wait --wait-timeout 240
$env:KONTUR_TEST_MONGO_URI = 'mongodb://127.0.0.1:27142/?directConnection=true&replicaSet=rs0'
$env:KONTUR_TEST_RABBIT_URI = 'rabbitmq://127.0.0.1:5673'
$env:KONTUR_TEST_RABBIT_MANAGEMENT_URI = 'http://127.0.0.1:15673'
$env:KONTUR_TEST_RABBIT_USER = 'webapi-test'
$env:KONTUR_TEST_RABBIT_PASSWORD = 'webapi-test-only'
dotnet build Auth/WebApplication1.sln -c Release
dotnet test Auth/BackEnd.Tests -c Release --no-build
dotnet test Auth/HabitTracker.Notifications.Tests -c Release --no-build --logger trx
docker compose -f compose.webapi-tests.yaml -p kontur-notifications-tests down -v
```

Для модульных тестов без инфраструктуры передайте `--filter 'Category!=Integration'`. Для клиента выполните `npm ci`, `npm test`, `npm run lint` и `npm run build` из `habit-frontend`. Браузерные сценарии без Worker можно проверить типизированным издателем `NotificationReadyV1`, как в `NotificationsIntegrationTests`.

## Подключение к стенду

Соберите образ из корня (`docker build -f Auth/HabitTracker.Notifications/Dockerfile -t habit-tracker-notifications:local .`). Манифест [Notifications](../deploy/k8s/notifications.yaml) задаёт HTTP-порт 80, проверки готовности и одну реплику со стратегией `Recreate`. Для стенда подставьте опубликованный образ и создайте в пространстве `habit-tracker` Secret `notifications` с перечисленными выше переменными. MongoDB и RabbitMQ должны быть доступны из пода. До запуска издателя импортируйте определения RabbitMQ.

Подставьте домен и имя TLS Secret в [манифест Ingress](../deploy/k8s/habit-tracker-ingress.yaml) и проверьте имена и порты сервисов `frontend`, `webapi` и `notifications`. Маршруты `/api/notifications` и `/hubs/notifications` ведут в Notifications. Traefik проксирует WebSocket Upgrade, а для отдельного маршрута Hub выключены access-логи и трассировка.

При Nginx вместо Ingress добавьте в `server` отдельные locations для этих префиксов. Для Hub используйте `proxy_http_version 1.1`, заголовки `Upgrade` и `Connection` и `proxy_read_timeout 120s`. Сохраните исходный путь при передаче в `http://notifications:80`. Для location Hub отключите access-лог и вывод ошибок с исходным URL.

Публичные логи прокси и атрибуты телеметрии должны исключать Authorization и query Hub. Для access-логов Traefik используйте JSON с отключёнными заголовками и полем `RequestPath`, если оно содержит query. Детали зависят от версии контроллера. [Настройка доступа к логам Traefik](https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/)

Проверка на внешнем стенде состоит из успешного WebSocket Upgrade, отправки с первым аккаунтом, повторного подключения после перезапуска пода, восстановления пропущенного уведомления и проверки изоляции вторым аккаунтом.
