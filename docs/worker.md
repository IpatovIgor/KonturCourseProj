# Запуск HabitTracker.Worker

Worker использует отдельную базу MongoDB **на replica set**. Сервисы могут пользоваться одним кластером, но задайте каждому своё имя базы и учётные данные. Предварительно подготовьте vhost и импортируйте [топологию RabbitMQ](../deploy/rabbitmq/definitions.json), чтобы события WebAPI попадали в очередь ещё до запуска Worker.

Передайте процессу следующие переменные через окружение, Compose или User Secrets. ASP.NET Core не загружает `.env` автоматически.

| Переменная | Значение |
|---|---|
| `MongoDbSettings__ConnectionString` | URI replica set для Worker |
| `MongoDbSettings__DatabaseName` | `habit_tracker_worker` или другое отдельное имя |
| `RabbitMq__Host` | `rabbitmq` в сети Compose, локально допустим `rabbitmq://127.0.0.1:5673` |
| `RabbitMq__VirtualHost` | `habit-tracker` |
| `RabbitMq__Username`, `RabbitMq__Password` | Учётные данные Worker |
| `WorkerApi__BaseUrl` | `http://webapi:80` в Compose, `http://localhost:5160` локально |
| `WorkerApi__SharedKey` | Тот же секрет, что на WebAPI |

Тайминги и принудительная загрузка перечислены в [плане 02](../plan/02-worker.md#конфигурация-worker). При настройке `Worker__BatchSize` учитывайте предел HTTP-страницы 200.

```powershell
dotnet run --project Auth/HabitTracker.Worker --no-launch-profile --urls http://localhost:5161
docker build -f Auth/HabitTracker.Worker/Dockerfile -t habit-tracker-worker:local Auth
```

Для общего Compose укажите `build.context: ./Auth` и `build.dockerfile: HabitTracker.Worker/Dockerfile`. Сервис слушает порт 80 в контейнере. Worker должен напрямую обращаться к настроенному адресу WebAPI по внутренней сети. Держите его в сети backend рядом с WebAPI, MongoDB и RabbitMQ. `/health/live` показывает доступность процесса. `/health/ready` проверяет bootstrap, MongoDB и подключение очереди Worker.

При штатном перезапуске Worker продолжит с сохранённых указателей и lease. После паузы с `Features__NotificationsEnabled=false` остановите все реплики Worker, включите WebAPI и запустите Worker с `Worker__ReloadSchedulesOnStartup=true`. После успешной загрузки верните параметр в `false` для следующих запусков. Если прежняя версия Worker успела подтверждать сообщения пустыми обработчиками, выполните такую же полную загрузку для восстановления текущих расписаний.

```powershell
dotnet build Auth/WebApplication1.sln -c Release
dotnet test Auth/HabitTracker.Worker.Tests -c Release --filter "Category!=Integration"
dotnet test Auth/HabitTracker.Worker.Tests -c Release --filter "Category=Integration" --logger trx
```

Для интеграций нужен запущенный Docker. Worker-тесты создают свои контейнеры MongoDB replica set и RabbitMQ. Для запуска интеграций WebAPI используйте отдельный стенд и `KONTUR_TEST_*` по [инструкции WebAPI](webapi-testing.md).
