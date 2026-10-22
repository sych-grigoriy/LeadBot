# Настройка перед первым запуском

Для первого запуска нужны только Telegram и DeepSeek. MAX и amoCRM выключены (`Max:Enabled`, `AmoCrm:Enabled` = `false`), лиды хранятся в памяти, Postgres не нужен.

Порт приложения задан в `src/LeadBot.Api/Properties/launchSettings.json`: `http://localhost:59881`.

## 1. Бот в Telegram (@BotFather)

1. `/newbot` → сохраните **токен** и **username** бота (например, `DsWebSerive_bot`).
2. `/mybots` → ваш бот → **Bot Settings**:
   - **Secretary Mode** → Turn On (раньше этот пункт назывался Business Mode);
   - **Allow Groups?** → включено;
   - **Group Privacy** → Turn off.
3. Свой user id узнайте у **@userinfobot**: нажмите Start, он ответит `Id: 123456789`.
4. Откройте своего бота и нажмите **Start**, иначе он не сможет присылать вам уведомления.

## 2. Ключ DeepSeek

На **platform.deepseek.com** зарегистрируйтесь, пополните баланс и создайте ключ в разделе **API Keys**.

Модель по умолчанию `deepseek-flash`. Если ответы слабые, поменяйте `Llm:DeepSeek:Model` на `deepseek-v4-pro`. Режим рассуждений (`EnableThinking`) не включайте: вместе с инструментами он требует пересылать рассуждения всех прошлых ходов, иначе API отвечает ошибкой 400.

Вернуться на Anthropic можно настройкой `Llm:Provider` = `Anthropic`.

## 3. Туннель (публичный HTTPS для вебхуков)

```powershell
winget install --id Cloudflare.cloudflared     # один раз, потом перезапустить терминал
cloudflared tunnel --url http://localhost:59881
```

Скопируйте выданный адрес `https://....trycloudflare.com`. Окно с туннелем держите открытым. При каждом перезапуске туннеля адрес меняется: заново запишите его в `Telegram:PublicBaseUrl` и перезапустите бота.

## 4. Секреты (PowerShell, в корне репозитория)

```powershell
$p = "src\LeadBot.Api"
dotnet user-secrets init --project $p
dotnet user-secrets set "Telegram:BotToken"       "123456:ABC..."                  --project $p
dotnet user-secrets set "Telegram:BotUsername"    "DsWebSerive_bot"                --project $p
dotnet user-secrets set "Telegram:WebhookSecret"  "придумайте_строку_A-Z_0-9_-"    --project $p
dotnet user-secrets set "Telegram:PublicBaseUrl"  "https://....trycloudflare.com"  --project $p
dotnet user-secrets set "Telegram:OwnerChatId"    "123456789"                      --project $p
dotnet user-secrets set "Llm:DeepSeek:ApiKey"     "sk-..."                         --project $p
```

- В `WebhookSecret` допустимы только латиница, цифры, `_` и `-`.
- Пока хотите тестировать как клиент со своего аккаунта, `OwnerChatId` не задавайте. Иначе бот примет вас за владельца и не станет вести с вами диалог.
- Секреты не вписывайте в `appsettings.json`: этот файл в git.

## 5. Описание компании

Заполните `src/LeadBot.Api/prompts/company.md`: услуги, ориентиры цен, реальные кейсы. Бот опирается только на этот текст, а заглушки вида `<сумма>` он будет повторять клиентам.

## 6. Запуск и проверка

1. Запустите проект из Visual Studio или командой `dotnet run --project src\LeadBot.Api`. В логе должна появиться строка **«Telegram webhook зарегистрирован»**.
2. Откройте `https://<туннель>/health`, должно вернуться `{"status":"ok"}`.
3. Откройте `https://api.telegram.org/bot<ТОКЕН>/getWebhookInfo`. В `url` должен быть ваш адрес с `/webhooks/telegram`, `last_error_message` должен быть пустым.
4. **Личка:** напишите боту с другого аккаунта, должен прийти ответ.
5. **Secretary Mode:** подключите бота в настройках аккаунта (раздел «Telegram для бизнеса» → «Чат-боты»; нужен Premium) и разрешите ему отвечать на сообщения. Затем напишите вам со второго аккаунта. Ответьте клиенту сами: бот должен замолчать.
6. **Группа:** добавьте бота через «Добавить участников» → поиск по username и упомяните `@DsWebSerive_bot`. Бот должен ответить кнопкой «Написать в личку».

## Если что-то не работает

| Симптом | Что проверить |
|---|---|
| Нет строки «webhook зарегистрирован» | `PublicBaseUrl` и токен; запущен ли туннель |
| `getWebhookInfo` показывает ошибку 502/530 | Туннель смотрит не на тот порт; нужен `59881` |
| Бот молчит, в логе `DeepSeek API 401/402` | Ключ или баланс DeepSeek |
| Ответ «Спасибо за сообщение! Передал коллеге…» | Ошибка LLM: подробности в логе выше |
| В группе не реагирует | Group Privacy выключили после добавления: удалите бота из группы и добавьте заново |

## Позже

- **amoCRM:** токен, ID этапов и полей (`StatusMap`, `LeadFields`), вебхук смены статуса → `AmoCrm:Enabled = true`. Подробности в [ARCHITECTURE.md](ARCHITECTURE.md#6-amocrm).
- **MAX:** верификация юрлица или ИП, токен от @MasterBot → `Max:Enabled = true`.
- **Postgres:** перед продом, сейчас всё теряется при перезапуске.
