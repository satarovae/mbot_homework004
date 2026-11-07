using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using ToDoBot.Handlers;
using ToDoBot.Services;

var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Не задан TELEGRAM_BOT_TOKEN. Укажите токен через переменную окружения и повторите запуск.");
    return 2;
}
var dataPath = Environment.GetEnvironmentVariable("TODO_DATA_PATH") ?? Path.Combine("data", "todo.json");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;
try
{
    var storage = new JsonToDoService(dataPath);
    var bot = new TelegramBotClient(token);
    var handler = new UpdateHandler(bot, storage, storage);
    var me = await bot.GetMe(ct);
    await bot.SetMyCommands(CommandCatalog.Commands, cancellationToken: ct);
    Console.WriteLine($"Бот @{me.Username} запущен. Для остановки нажмите Ctrl+C.");
    int? offset = null;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var updates = await bot.GetUpdates(offset: offset, timeout: 30,
                allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: ct);
            foreach (var update in updates)
            {
                try { await handler.HandleUpdateAsync(bot, update, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"Ошибка обработки сообщения ({ex.GetType().Name})."); }
                offset = update.Id + 1;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (ApiRequestException ex) when (ex.ErrorCode is 401 or 409)
        {
            Console.Error.WriteLine("Telegram отклонил запуск: проверьте токен, отсутствие второго экземпляра и активного webhook.");
            return 3;
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("Telegram временно недоступен. Повтор через 3 секунды.");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
catch (Exception ex)
{
    Console.Error.WriteLine($"Запуск не выполнен ({ex.GetType().Name}). Проверьте токен, доступ к Telegram и файл данных.");
    return 1;
}
return 0;
