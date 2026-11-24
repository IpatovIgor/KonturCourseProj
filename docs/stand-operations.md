# Остановка и запуск Habit Tracker

Учебный стенд работает на VM `habit-tracker-vm` в Yandex Cloud. Приложение доступно по адресу [habit-tracker.duckdns.org](https://habit-tracker.duckdns.org/). MongoDB и RabbitMQ используют постоянные тома k3s на диске VM.

## Остановить стенд

1. Дождись окончания текущей выкладки. Если настроена выкладка по SSH, поставь переменную репозитория `DEPLOY_OVER_SSH=false` перед длительной остановкой. Тесты и публикация образов в GitHub Actions могут продолжаться при выключенной VM.
2. В консоли Yandex Cloud открой **Compute Cloud → Виртуальные машины → habit-tracker-vm → Остановить**. Дождись статуса `Stopped`. На время остановки сайт и отправка напоминаний недоступны.
3. Сохрани VM, диски и PVC для последующего запуска. В статусе `Stopped` тарификация vCPU и RAM прекращается. Диски, зарезервированный IP и хранилище Container Registry оплачиваются отдельно. [Остановка VM](https://yandex.cloud/ru/docs/compute/operations/vm-control/vm-stop-and-start) · [Тарификация](https://yandex.cloud/ru/docs/billing/qa/all) · [Тарификация Registry](https://yandex.cloud/ru/docs/container-registry/pricing)

## Запустить стенд

1. В той же консоли нажми **Запустить** и дождись статуса `Running`. Статический IP сохраняется. Если адрес динамический, сверяй новый публичный IP с записью DuckDNS. [Публичные IP](https://yandex.cloud/ru/docs/vpc/concepts/address)
2. Подключись по SSH и дождись готовности узла, приложений и хранилищ:

   ```bash
   systemctl is-active k3s
   sudo k3s kubectl get nodes
   sudo k3s kubectl -n habit-tracker get pods,pvc
   sudo k3s kubectl -n habit-tracker get certificate habit-tracker
   ```

   На штатной установке k3s стартует вместе с VM. У приложений и хранилищ ожидается `Running` с готовыми контейнерами, у PVC — `Bound`. [Автозапуск k3s](https://docs.k3s.io/quick-start)
3. Проверь публичный адрес:

   ```bash
   curl -fsS -o /dev/null -w '%{http_code}\n' https://habit-tracker.duckdns.org/
   ```

   Ожидается `200`. При проблемах проверь события и логи нужного приложения:

   ```bash
   sudo k3s kubectl -n habit-tracker get events --sort-by=.lastTimestamp
   sudo k3s kubectl -n habit-tracker logs deployment/worker --since=10m
   ```

## Что происходит с CI/CD при остановке VM

| Действие | Где выполняется | VM нужна |
|---|---|---|
| Работа над кодом и локальные тесты | Компьютер разработчика | Нет |
| CI для pull request и проверка `main` | GitHub-hosted runner | Нет |
| Публикация образов и подготовка артефакта `release-<SHA>-<attempt>` | GitHub-hosted runner | Нет |
| Применение релиза, проверка HTTPS и откат при сбое | На VM через SSH из GitHub Actions или вручную | Да |

При `DEPLOY_OVER_SSH=true` после публикации GitHub подключается к VM. Выключенная VM сделает шаг `deploy` ошибочным, а артефакт останется доступным. После запуска VM можно включить выкладку и повторно запустить релиз для актуальной `main` или [применить артефакт вручную](deployment.md#обновить-приложение-из-артефакта).

## Выпустить новую версию

1. Для публикации GitHub Actions использует `YCR_PUSHER_KEY`. Окружение `production` можно ограничить веткой `main`. Ключ скачивания образов хранится на VM в Kubernetes Secret `ycr-pull`, а kubeconfig используется локально при выкладке. [Подготовка инфраструктуры и секретов](deployment.md)
2. После слияния PR дождись успешного workflow **Prepare release for k3s**. Для повторной публикации текущей версии запусти его вручную для `main`.
3. Для автоматического обновления включи VM и [настрой подключение GitHub по SSH](deployment.md#подключить-выкладку-через-ssh). Для ручного обновления примени сохранённый артефакт. Храни релизы по SHA, чтобы выбрать предыдущий при откате. [Команды ручной выкладки и отката](deployment.md#обновить-приложение-из-артефакта)
