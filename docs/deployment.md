# Запуск на сервере (Docker)

Мост работает как один контейнер. Образ собирает CI и публикует в Docker Hub, на сервере достаточно Docker и одной
команды `docker run`.

## Что нужно заранее

1. **Группа Telegram с темами и бот-администратор.** Как их создать и где взять ID группы, написано в
   [telegram-bridge.md](telegram-bridge.md), раздел «Настройка».
2. **Токен и ID устройства MAX.** Откройте web.max.ru в браузере и войдите в аккаунт. Затем DevTools (F12) →
   Application → Local Storage → `https://web.max.ru`, скопируйте значения ключей `__oneme_auth` (это токен) и
   `__oneme_device_id`. Токен может перестать действовать, если выйти из MAX в браузере или зайти с другого
   устройства: тогда мост напишет об этом в тему General, и значения нужно взять заново.

## Запуск

Подставьте свои значения и выполните на сервере:

```bash
docker run -d \
  --name max-bridge \
  --restart unless-stopped \
  -e TelegramConfig__ApiToken="123456:ABC-токен-бота" \
  -e TelegramConfig__GroupChatId="-1001234567890" \
  -e MaxConfig__Token="токен-из-__oneme_auth" \
  -e MaxConfig__DeviceId="значение-из-__oneme_device_id" \
  -v max-bridge-state:/app/state \
  ВАШ_ЛОГИН_DOCKERHUB/bridge-telegram-to-max:latest
```

Что здесь важно:

- `--restart unless-stopped` поднимает мост после перезагрузки сервера и после сбоя.
- `-v max-bridge-state:/app/state` хранит соответствие «чат MAX — тема Telegram» и последние сообщения. Без тома
  после пересоздания контейнера мост создал бы все темы заново и повторно переслал последние сообщения.
- Порты открывать не нужно: мост сам подключается к MAX и Telegram, входящих подключений у него нет.

## Настройки

Настройки передаются переменными окружения. Вложенные имена пишутся через двойное подчёркивание.

| Переменная | Нужна | Что это |
|---|---|---|
| `TelegramConfig__ApiToken` | да | токен бота от @BotFather |
| `TelegramConfig__GroupChatId` | да | ID группы с темами, число вида `-100xxxxxxxxxx` |
| `MaxConfig__Token` | да | токен MAX (`__oneme_auth`) |
| `MaxConfig__DeviceId` | да | ID устройства MAX (`__oneme_device_id`) |
| `TelegramConfig__MaxUploadMegabytes` | нет | предел размера файла, который бот отправляет в Telegram, по умолчанию 50 |
| `MaxConfig__InitialMessageCount` | нет | сколько последних сообщений показать при первом запуске, по умолчанию 20, `0` — не показывать |
| `MaxConfig__FileProcessingTimeoutSeconds` | нет | сколько ждать, пока MAX обработает загруженный файл, по умолчанию 60 |
| `MaxConfig__AppVersion`, `MaxConfig__UserAgent` | нет | версия приложения и User-Agent, под которые бот выдаёт себя за веб-клиент MAX; меняются, если MAX перестанет принимать вход |

Токены видны любому, у кого есть доступ к Docker на сервере (`docker inspect`), и остаются в истории оболочки. Если
это важно, запускайте команду из файла-скрипта с правами только для вас или через оркестратор с хранилищем секретов.

## Повседневная работа

```bash
docker logs -f max-bridge            # журнал
docker stop max-bridge               # остановить
docker start max-bridge              # запустить снова
```

Обновление до свежего образа (состояние остаётся в томе):

```bash
docker pull ВАШ_ЛОГИН_DOCKERHUB/bridge-telegram-to-max:latest
docker rm -f max-bridge
# затем снова команда docker run из раздела «Запуск»
```

Если обязательная настройка не задана или записана неверно (например, `GroupChatId` не число), контейнер сразу
завершается с кодом 1 и перечисляет проблемные переменные, например `MaxConfig__Token: не задана` (`docker logs max-bridge`).
Если неверен токен бота или бот не администратор группы с темами, мост пишет об этом в журнал и тоже завершается; при
`--restart unless-stopped` Docker будет пробовать запустить его снова с растущими паузами, так что после исправления
настроек в Telegram мост поднимется сам.

Если MAX отклонил токен, контейнер **не завершается** (иначе автоперезапуск гонял бы его по кругу и засорял тему
General): в General приходит одно сообщение, а в журнале ошибка. Возьмите новые значения, удалите контейнер
(`docker rm -f max-bridge`) и запустите заново с новым токеном.

## Откуда берётся образ

Образ собирает GitHub Actions ([.github/workflows/docker.yml](../.github/workflows/docker.yml)) и публикует в Docker Hub как
`<ваш логин>/bridge-telegram-to-max`:

| Событие | Что происходит |
|---|---|
| push в `main` | публикуются теги `latest` и `sha-<короткий хеш коммита>` |
| тег `v1.2.3` | дополнительно публикуются `1.2.3` и `1.2` |
| pull request | образ только собирается (проверка), в Docker Hub ничего не уходит |

Образ собирается в конфигурации Release под `linux/amd64` (обычный x86-64 VPS, в том числе Debian). Для сервера на ARM его нужно собирать самому (раздел ниже), добавив в `docker build` флаг `--platform linux/arm64`.

**Один раз настройте** секреты репозитория на GitHub (Settings → Secrets and variables → Actions → New repository secret):

- `DOCKERHUB_USERNAME`: ваш логин на Docker Hub;
- `DOCKERHUB_TOKEN`: токен доступа (Docker Hub → Account settings → Personal access tokens, права Read & Write).

Репозиторий на Docker Hub создаётся при первой публикации. Чтобы закрепить версию на сервере, вместо `latest`
укажите конкретный тег, например `sha-1a2b3c4` или `1.2.3`.

## Сборка образа вручную

Из корня репозитория:

```bash
docker build -f src/Dockerfile -t bridge-telegram-to-max .
```

Затем в команде `docker run` вместо `ВАШ_ЛОГИН_DOCKERHUB/bridge-telegram-to-max:latest` укажите `bridge-telegram-to-max`.
