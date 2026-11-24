# Выкладка Habit Tracker на k3s

Стенд использует домен **habit-tracker.duckdns.org** и реестр **cr.yandex/crp2k95esbq4ajsrt5d6**. Команды выполняются из корня репозитория в Bash с доступом к целевому k3s. Для первого запуска нужны `kubectl`, `openssl`, `jq`, Traefik, cert-manager, класс хранения `local-path` и штатный домен кластера `cluster.local`. На одной VM MongoDB и RabbitMQ используют по одной реплике.

## Проверка кластера

```sh
kubectl config current-context
kubectl get nodes
kubectl get storageclass local-path
kubectl get ingressclass traefik
kubectl -n kube-system get pods -l app.kubernetes.io/name=traefik
kubectl -n cert-manager rollout status deploy/cert-manager --timeout=120s
kubectl -n cert-manager rollout status deploy/cert-manager-webhook --timeout=120s
kubectl get crd middlewares.traefik.io issuers.cert-manager.io certificates.cert-manager.io
```

Проверьте, что DNS указывает на публичный IP VM и до Traefik доступны TCP-порты `80/443`. cert-manager использует HTTP-01 на порту 80. Ingress приложения перенаправляет обычные HTTP-запросы на HTTPS, а маршрут проверки сертификата получает приоритет. TLS завершается в Traefik, который обращается к приложениям по HTTP. Для WebAPI перенаправление HTTPS внутри контейнера выключено.

## Секреты и хранилище

```sh
kubectl apply -f deploy/k8s/infra/namespace.yaml
bash deploy/k8s/create-secrets.sh

kubectl -n habit-tracker create configmap rabbitmq-topology \
  --from-file=deploy/rabbitmq/definitions.json \
  --from-file=deploy/rabbitmq/bootstrap.sh \
  --from-file=deploy/rabbitmq/bootstrap-k8s.sh \
  --dry-run=client -o yaml | kubectl apply -f -
```

Скрипт генерирует только отсутствующие Secrets. Сохраните резервные копии паролей и keyfile MongoDB вне VM. При существующих PVC используйте согласованные со стендом данные и секреты. Изменение секрета администратора само по себе не меняет пароль в сохранённой базе.

Для приватных образов нужен `ycr-pull` с ключом сервисного аккаунта, которому выдана роль `container-registry.images.puller` на реестр. Переменная `YCR_PULL_KEY_FILE` указывает на локальный файл авторизованного JSON-ключа. `jq` создаёт Docker config, а `kubectl` читает его из временного файла. [Настройка Kubernetes Secret для Container Registry](https://yandex.cloud/ru/docs/managed-kubernetes/operations/private-registries)

```sh
(
  set -e
  umask 077
  pull_config=$(mktemp)
  trap 'rm -f "$pull_config"' EXIT
  jq -n --rawfile key "${YCR_PULL_KEY_FILE:?Set the path to the registry pull key}" \
    '{auths: {"cr.yandex": {auth: (("json_key:" + $key) | @base64)}}}' > "$pull_config"
  kubectl -n habit-tracker create secret generic ycr-pull \
    --type=kubernetes.io/dockerconfigjson \
    --from-file=".dockerconfigjson=$pull_config"
)
```

Размеры PVC для первого стенда заданы как `5Gi` для MongoDB и `2Gi` для RabbitMQ. Сверьте их с диском VM до создания PVC. Для `local-path` убедитесь, что каталог provisioner расположен на диске для данных. Значения PVC в манифестах не задают лимит потребления места на файловой системе.

После замеров заполните `resources.requests.cpu`, `resources.requests.memory`, `resources.limits.cpu` и `resources.limits.memory` у контейнеров WebAPI, Worker, Notifications, frontend, MongoDB и RabbitMQ. До замеров используются настройки кластера по умолчанию.

## Инициализация инфраструктуры

```sh
kubectl apply --dry-run=server -k deploy/k8s/infra
kubectl apply -k deploy/k8s/infra
kubectl -n habit-tracker rollout status statefulset/rabbitmq --timeout=300s

kubectl apply --dry-run=server -k deploy/k8s/bootstrap
kubectl apply -k deploy/k8s/bootstrap
kubectl -n habit-tracker wait --for=condition=complete job/mongo-bootstrap job/rabbitmq-bootstrap --timeout=600s
kubectl -n habit-tracker rollout status statefulset/mongo --timeout=120s
```

MongoDB станет `Ready` после инициализации replica set заданием `mongo-bootstrap`. Job также создаёт три учётные записи с ролью `readWrite` на соответствующих базах. Задание RabbitMQ запускает существующую проверку топологии внутри pod и настраивает учётную запись AMQP. Его ServiceAccount имеет доступ к `exec` только в `rabbitmq-0`. При повторной инициализации удалите завершённые Jobs по имени и примените каталог `bootstrap` заново. PVC при этом сохраняются.

## Обновить приложение из артефакта

После слияния в `main` workflow **Prepare release for k3s** повторно запускает CI, публикует четыре образа и готовит артефакт `release-<SHA>-<attempt>`. В нём есть `habit-tracker-release.yaml`, `release-digests.txt` и `apply-release.sh`. Открой успешный запуск в GitHub Actions и скачай артефакт из раздела **Artifacts**. [Скачивание артефактов GitHub Actions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)

Включи VM и перенеси на неё распакованную папку релиза. Из PowerShell укажи путь к этой папке:

```powershell
$release = Read-Host 'Путь к распакованной папке release'
ssh andrelin@153.51.174.162 'mkdir -p ~/habit-tracker/releases'
scp -r "$release" andrelin@153.51.174.162:habit-tracker/releases/
```

На VM зайди в папку релиза. Для скрипта нужны `kubectl`, `jq` и `curl`. Проверь SHA в `release-digests.txt` и примени релиз:

```bash
cat release-digests.txt
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml bash ./apply-release.sh
```

Скрипт предназначен для обновления уже работающего стенда. Он выполняет серверный dry-run, сохраняет предыдущие ресурсы приложения, обновляет приложения по порядку и проверяет сертификат, frontend и ответы защищённых API. При сбое скрипт пытается восстановить предыдущие ресурсы, а состояние подов и результат отката записывает в `deploy-diagnostics.log`. Дожидайся завершения одной выкладки перед началом следующей.

Для проверки JWT, доставки через WebSocket и офлайн-истории запусти из корня репозитория на компьютере с Node.js 24:

```bash
npm --prefix habit-frontend ci
node habit-frontend/scripts/smoke-release.mjs https://habit-tracker.duckdns.org/
```

Smoke-тест создаёт два технических аккаунта и архивирует созданные привычки после проверки. Аккаунты и история сохраняются. Если проверка выявила сбой, примени распакованный артефакт предыдущего успешного релиза.

## Подключить выкладку через SSH

GitHub-hosted runner скачивает артефакт проверенного SHA и передаёт манифест по SSH. На VM выделенный пользователь `habit-deploy` принимает команду `deploy <SHA>` и запускает установленный скрипт выкладки. Ключ допускает только проверку доступа и эту команду. Кластерные credentials хранятся на VM. Ручная выкладка из артефакта доступна как запасной путь.

### Подготовить VM и SSH-ключ

Из PowerShell в корне проекта создай отдельный ключ. Для автоматического запуска оставь пароль ключа пустым. Передай на VM файлы настройки и **публичную** часть ключа:

```powershell
$key = "$env:USERPROFILE\.ssh\habit-tracker-deploy"
ssh-keygen -t ed25519 -C habit-tracker-deploy -f $key
ssh andrelin@153.51.174.162 'mkdir -p ~/habit-tracker/ssh-setup'
scp .\deploy\k8s\apply-release.sh .\deploy\k8s\receive-release.sh .\deploy\k8s\setup-ssh-deploy.sh .\deploy\k8s\cd-access.yaml "${key}.pub" andrelin@153.51.174.162:habit-tracker/ssh-setup/
```

На VM с уже запущенным стендом выполни:

```bash
cd ~/habit-tracker/ssh-setup
sudo apt install -y jq curl
sudo bash ./setup-ssh-deploy.sh ./habit-tracker-deploy.pub
sudo cat /etc/ssh/ssh_host_ed25519_key.pub
sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Скрипт создаёт пользователя без sudo, устанавливает команды и настраивает Kubernetes-доступ к существующим ресурсам приложения в `habit-tracker`. Используется временный токен ServiceAccount. Запрашивается срок семь суток, кластер может выдать токен на меньший срок. Для обновления токена повтори команду `setup-ssh-deploy.sh` с тем же публичным ключом. При изменении скрипта выкладки или списка ресурсов сначала обнови файлы настройки на VM и повтори установку.

Проверь подключение из PowerShell. При первом подключении сверь отпечаток сервера с выводом `ssh-keygen -lf` на VM:

```powershell
ssh -i $key -o IdentitiesOnly=yes habit-deploy@habit-tracker.duckdns.org check
```

### Включить выкладку в GitHub

Ограничь окружение `production` веткой `main`. Для SSH добавь секрет в это окружение, а переменные задай в настройках репозитория **Settings → Secrets and variables → Actions → Variables**. Workflow использует существующий секрет `YCR_PUSHER_KEY` для Container Registry.

| Тип | Имя | Значение |
|---|---|---|
| Секрет окружения | `DEPLOY_SSH_KEY` | Содержимое приватного файла `habit-tracker-deploy` |
| Переменная репозитория | `DEPLOY_SSH_KNOWN_HOSTS` | Строка `habit-tracker.duckdns.org ssh-ed25519 AAAA…` с публичным ключом хоста, полученным на VM |
| Переменная репозитория | `DEPLOY_SSH_HOST` | По умолчанию `habit-tracker.duckdns.org`. Укажи адрес, если используешь другой |
| Переменная репозитория | `DEPLOY_SSH_PORT` | По умолчанию `22`. Для другого порта запиши хост в known_hosts как `[имя-хоста]:порт` |

GitHub-hosted runner должен иметь сетевой маршрут к SSH-порту. Проверка из PowerShell подтверждает доступ с твоего компьютера. Исходящие IP стандартных runner меняются, поэтому для ограниченного SSH-доступа используй согласованный сетевой маршрут, например VPN. [Сетевой доступ runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#ip-addresses)

После настройки добавь **переменную репозитория** `DEPLOY_OVER_SSH=true`, включи VM и запусти **Prepare release for k3s** для `main`. Проверь результаты `verify`, `publish` и `deploy`. При выключенной VM можно поставить `DEPLOY_OVER_SSH=false`: тесты и публикация образов продолжатся, а артефакт останется доступным.

## Первая выкладка и рендер по digest

Соберите и опубликуйте образы `webapi`, `worker`, `notifications` и `frontend` проверенного коммита в указанном Registry. Передайте их digest в том же порядке. При необходимости укажите свой контакт в `spec.acme.email` в `deploy/k8s/certificates.yaml`.

```sh
bash deploy/k8s/render-release.sh \
  "$WEBAPI_DIGEST" "$WORKER_DIGEST" "$NOTIFICATIONS_DIGEST" "$FRONTEND_DIGEST" > habit-tracker-release.yaml
kubectl apply --dry-run=server -f habit-tracker-release.yaml
kubectl diff -f habit-tracker-release.yaml
```

`kubectl diff` возвращает код 1 при наличии запланированных изменений. Для первой выкладки примените весь набор и дождитесь готовности каждого приложения.

```sh
(
  set -e
  kubectl apply -f habit-tracker-release.yaml
  for app in webapi worker notifications frontend; do
    kubectl -n habit-tracker rollout status "deployment/$app" --timeout=360s
  done
  kubectl -n habit-tracker wait --for=condition=ready certificate/habit-tracker --timeout=300s
  redirect=$(curl -fsS -o /dev/null -w '%{http_code} %{redirect_url}' http://habit-tracker.duckdns.org/)
  if [[ $redirect != '301 https://habit-tracker.duckdns.org/' && $redirect != '308 https://habit-tracker.duckdns.org/' ]]; then
    printf 'Unexpected HTTP redirect: %s\n' "$redirect" >&2
    exit 1
  fi
  curl -fsS https://habit-tracker.duckdns.org/
)
```

Перед обновлением сохраните согласованную резервную копию MongoDB и определения RabbitMQ. Если меняется топология RabbitMQ, обновите ConfigMap, повторите `rabbitmq-bootstrap` и дождитесь его готовности. После проверки dry-run и diff обновляйте потребителей перед издателями.

```sh
(
  set -e
  for app in notifications worker webapi frontend; do
    kubectl apply -f habit-tracker-release.yaml -l "app=$app"
    kubectl -n habit-tracker rollout status "deployment/$app" --timeout=360s
  done
  kubectl apply -f habit-tracker-release.yaml
)
```

Сохраните YAML каждого успешного релиза. Чтобы откатить приложения, примените YAML предыдущего релиза с его digest и повторите rollout и smoke-проверку. Перед откатом на образы до спринта 3 выполните [миграцию отката](../plan/05-deployment.md#откат-на-версию-до-спринта-3).

После запуска проверьте регистрацию, список привычек, SignalR и доставку напоминания. Проверьте, что публичный `/internal/v1/habit-schedules` возвращает `404`, а RabbitMQ Management доступна через административный `kubectl port-forward -n habit-tracker svc/rabbitmq 15672:15672`. Панели телеметрии подключаются по [отдельному плану](../plan/06-observability.md).
