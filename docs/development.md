# Добавление и изменение модулей

## Ветки и pull request

1. Начинай задачу от актуальной `origin/main` в отдельной ветке:

   ```bash
   git fetch origin
   git switch -c feature/habit-module origin/main
   ```

2. Держи один PR в рамках одной задачи. В описании укажи, что изменилось, как проверить сценарий и как выполнить откат, если меняются данные или инфраструктура.
3. Запушь ветку и открой PR в `main`. Перед слиянием дождись ревью и зелёного `WebAPI tests / webapi`. Для обязательной проверки владелец выбирает этот status check с первого выполненного PR и включает требование PR для `main`. [Защита веток GitHub](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
4. Слияние запускает повторную проверку итогового SHA. После её успеха workflow публикует образы и готовит артефакт. При включённом SSH-деплое GitHub применяет его на VM. Тот же артефакт можно применить вручную. VM можно держать выключенной во время разработки, проверки PR и публикации. [Остановка и запуск стенда](stand-operations.md)

## Границы модулей

- Размещай логику привычек и авторизации в `Auth/WebApplication1`, расписание и задачи в `Auth/HabitTracker.Worker`, историю уведомлений и SignalR в `Auth/HabitTracker.Notifications`, UI в `habit-frontend`.
- Подключай новый .NET-проект и его тестовый проект к `Auth/WebApplication1.sln`. Поведение модуля проверяй модульными тестами. Тесты, которым нужны MongoDB, RabbitMQ или полный хост, помечай `Category=Integration`. Для нового тестового проекта обнови списки запуска в [CI](../.github/workflows/webapi-tests.yml), список обязательных сценариев и сборок в [проверке отчётов](../scripts/check-ci-results.py) и фильтры в [coverage.runsettings](../Auth/coverage.runsettings).
- Для отдельного сервиса обнови Dockerfile, Compose и k3s вместе с тестами. Если он выкатывается на стенд, добавь его в публикацию, рендер релиза и порядок rollout. Для публичного маршрута проверь Ingress. Для нового сетевого соединения проверь NetworkPolicy, а для обязательной конфигурации укажи переменные и ссылки на Secrets.
- Сохраняй совместимость действующих API и событий `v1` при поэтапном обновлении. Для несовместимого события заведи новую версию. Миграции должны поддерживать работу со старыми данными, повторное выполнение и заранее описанный откат. Сначала обновляй совместимых потребителей, затем издателей. [Архитектура и контракты](../plan/README.md)
- Храни секреты в GitHub Environment и Kubernetes Secrets. В PR указывай имена новых параметров и секретов, которые понадобятся при выкладке.

## Проверка перед PR

```bash
dotnet build Auth/WebApplication1.sln -c Release -p:RunAnalyzersDuringBuild=true
dotnet test Auth/WebApplication1.sln -c Release --no-build --filter 'Category!=Integration'
npm --prefix habit-frontend ci
npm --prefix habit-frontend run lint
npm --prefix habit-frontend test
npm --prefix habit-frontend run build
```

Для интеграционных тестов используй отдельные MongoDB и RabbitMQ из [тестовой инструкции](webapi-testing.md). CI проверяет наличие и успех обязательных сценариев всех трёх сервисов по TRX и собирает Docker-образы для PR.

В отчёте `ci-results` есть TRX и модульное покрытие. Порог курса — **строго выше 60% строк**, сейчас он выводится как предупреждение. После достижения порога владелец включает обязательную проверку переменной GitHub Actions `KONTUR_ENFORCE_COVERAGE=true`. Форматирование пока проверяется для C#-файлов, изменённых в PR или коммите `main`, чтобы исправлять накопленные нарушения вместе с изменениями соответствующих файлов.
