# Временные отключения и уборка после спринта 3

В спринте 3 дневная сводка выключена флагом во всех трёх бэкенд-сервисах. Настройки пользователей и история сохраняются. Обычные напоминания доставляются по расписанию с учётом отметок. Утренний список запланированных привычек и вечернее уведомление о невыполненных остаются отдельными сценариями спринта 4.

## Как вернуть дневную сводку

1. В корневом `.env` задать `HABIT_TRACKER_DAILY_SUMMARY_ENABLED=true` и выполнить `docker compose up -d --wait`. Если менялся код, использовать `docker compose up -d --build --wait`. Для облачного стенда передать `Features__DailySummaryEnabled=true` в WebAPI, Worker и Notifications вместе с остальной конфигурацией релиза.
2. В интерфейсе открыть «Дневная сводка», включить её для тестового аккаунта и выбрать ближайшее будущее время по часовому поясу аккаунта. Флаг открывает функцию на стенде. `GET /api/profile/summary-settings` возвращает отдельные настройки пользователя, у нового аккаунта сводка выключена.
3. Проверить, что сводка появляется один раз через SignalR и в истории. Проверить обычное напоминание и его пропуск при отметке до срока по [сценариям приёмки](07-ci-cd-and-acceptance.md#матрица-сценариев-для-спринтов).

## Где стоит временное отключение

| Место | Что проверяет флаг |
|---|---|
| [`.env.example`](../.env.example), [`compose.yaml`](../compose.yaml) | `HABIT_TRACKER_DAILY_SUMMARY_ENABLED` по умолчанию равен `false`. Compose передаёт его всем трём сервисам как `Features__DailySummaryEnabled`. |
| [`NotificationFeatures.cs`](../Auth/WebApplication1/Services/NotificationFeatures.cs), [`Program.cs`](../Auth/WebApplication1/Program.cs), [`NotificationSettingsEndpointAttribute.cs`](../Auth/WebApplication1/Controllers/NotificationSettingsEndpointAttribute.cs), [`ProfileController.cs`](../Auth/WebApplication1/Controllers/ProfileController.cs), [`WorkerSchedules.cs`](../Auth/WebApplication1/Services/WorkerSchedules.cs) | Прячет GET/PUT настроек сводки и запрещает её отправку во внутренней проверке Worker. |
| [`WorkerOptions.cs`](../Auth/HabitTracker.Worker/Configuration/WorkerOptions.cs), [`Program.cs`](../Auth/HabitTracker.Worker/Program.cs), [`SchedulerService.cs`](../Auth/HabitTracker.Worker/Services/SchedulerService.cs), [`JobExecutorService.cs`](../Auth/HabitTracker.Worker/Services/JobExecutorService.cs) | Останавливает планирование и пропускает уже созданные задания сводки. |
| [`NotificationReadyConsumer.cs`](../Auth/HabitTracker.Notifications/Consumers/NotificationReadyConsumer.cs) | Подтверждает события `dailySummary` из очереди, пропуская запись и отправку. |
| [`HabitTracker.jsx`](../habit-frontend/src/HabitTracker.jsx), [`SummarySettings.jsx`](../habit-frontend/src/notifications/SummarySettings.jsx), [`SettingsForm.jsx`](../habit-frontend/src/notifications/SettingsForm.jsx) | React скрывает блок, когда запрос настроек получает `404`. Своего флага у фронтенда нет. |

## Что убрать после приёмки спринта 4

**Если сводка остаётся в продукте и отдельный выключатель больше не нужен**, убрать обе переменные из шаблонов, локальной и облачной конфигурации, затем убрать проверки этого флага из перечисленного кода. Обновить описание в [README](../README.md) и [контракте API](../docs/api.md#настройки-уведомлений). Сохранить общий флаг `Features__NotificationsEnabled`, проверку `user.SummaryEnabled`, форму настроек и историю. Удалить тесты выключенного режима сводки из `WorkerScheduleHttpTests.cs`, `ExecutionTests.cs` и `NotificationsIntegrationTests.cs` и соответствующие параметры фикстур. В React сохранить обработку `404`, пока общий флаг может скрыть маршруты.

**Если новая функция заменяет старую дневную сводку**, сначала проверить ссылки и обращения к её API. Кандидаты на удаление после замены — `SummarySettings.jsx`, текущий `ProfileController.cs`, [`SummarySettingsChangedConsumer.cs`](../Auth/HabitTracker.Worker/Consumers/SummarySettingsChangedConsumer.cs) и [`SummarySchedule.cs`](../Auth/HabitTracker.Worker/Models/SummarySchedule.cs). В том же изменении убрать их вызовы, регистрации и тесты. Перед удалением `SummarySettingsChangedV1` проверить невычитанные сообщения и outbox, затем согласованно обновить RabbitMQ bindings, bootstrap, replay и тесты. Сохранить общую очередь `worker-habit-events-v1`, SignalR и обработку уже сохранённых уведомлений `dailySummary` в истории.

**Скрипт отката** [`rollback-reminder-date.js`](../scripts/rollback-reminder-date.js) можно удалить вместе со ссылками на него в планах [деплоя](05-deployment.md#откат-на-версию-до-спринта-3) и [CI/CD](07-ci-cd-and-acceptance.md#доставка-только-проверенного-коммита), когда команда перестанет поддерживать откат на образы до спринта 3.

После уборки выполнить поиск `DailySummaryEnabled|DAILY_SUMMARY_ENABLED|daily_summary_disabled`, проверить оставшиеся ссылки и прогнать backend и frontend тесты. На стенде проверить напоминание, пропуск выполненной привычки, доставку через SignalR и чтение старой истории.
