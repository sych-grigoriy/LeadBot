# LeadBot

ИИ-бот, который сам ведёт лидов по воронке в Telegram (включая Telegram Business) и MAX и синхронизирует их с amoCRM. Каркас на .NET 10 / ASP.NET Core.

Архитектура, воронка, настройка каналов и amoCRM — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Структура

```
src/
  LeadBot.Domain                 лид, стадии, машина состояний воронки
  LeadBot.Application            оркестратор диалога, LLM-агент с инструментами, очереди, воркеры
  LeadBot.Channels.Telegram      Bot API: Business, личка, группы, уведомления владельцу
  LeadBot.Channels.Max           MAX Bot API
  LeadBot.Crm.AmoCrm             amoCRM API v4 + вебхук смены статуса
  LeadBot.Llm.Anthropic          ILlmClient для Anthropic Messages API
  LeadBot.Infrastructure         InMemory-хранилище, календарь, часы
  LeadBot.Persistence.Postgres   EF Core + Npgsql (prod)
  LeadBot.Api                    хост, вебхуки, appsettings, prompts/company.md
tests/
  LeadBot.Domain.Tests           xUnit-тесты воронки
```

## Быстрый старт

1. Создайте бота в @BotFather и включите ему Business Mode (Bot Settings → Business Mode).
2. Заполните секреты: `Telegram:BotToken`, `Telegram:BotUsername`, `Telegram:WebhookSecret`, `Telegram:PublicBaseUrl`, `Telegram:OwnerChatId`, `Llm:Anthropic:ApiKey`, `Llm:Anthropic:Model`.
3. Заполните `src/LeadBot.Api/prompts/company.md`: услуги, ориентиры цен, реальные кейсы.
4. `dotnet run --project src/LeadBot.Api`. Вебхук Telegram зарегистрируется сам.
5. В Telegram: Настройки → Telegram для бизнеса → Чат-боты → ваш бот.
6. amoCRM и MAX включаются флагами `AmoCrm:Enabled` и `Max:Enabled` после заполнения их секций.

Для prod-хранилища подключите `LeadBot.Persistence.Postgres` (раскомментируйте ссылку в `LeadBot.Api.csproj` и вызов `AddPostgresPersistence` в `Program.cs`), задайте `ConnectionStrings:LeadBot` и создайте миграцию.
