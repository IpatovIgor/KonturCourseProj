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

## Приложения и HTTPS

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
