using System.Collections.Concurrent;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ToDoBot.Callbacks;
using ToDoBot.Helpers;
using ToDoBot.Models;
using ToDoBot.Scenarios;
using ToDoBot.Services;

namespace ToDoBot.Handlers;

public sealed class UpdateHandler(ITelegramBotClient bot, IToDoService tasks, IToDoListService lists)
{
    private static int _pageSize = 5;
    private readonly ConcurrentDictionary<(long Chat, long User), Draft> _drafts = new();
    private readonly ConcurrentDictionary<(long Chat, long User), PendingDelete> _deletions = new();
    private readonly ConcurrentDictionary<(long Chat, long User), View> _views = new();
    private readonly IScenario _deleteTask = new DeleteTaskScenario(tasks);
    private readonly IScenario _deleteList = new DeleteListScenario(lists);

    public Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct) =>
        update.CallbackQuery is { } query ? OnCallbackQuery(query, ct) :
        update.Message is { } message ? OnMessage(message, ct) : Task.CompletedTask;

    public async Task OnMessage(Message message, CancellationToken ct)
    {
        try { await OnMessageCore(message, ct); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (message.Chat.Type == ChatType.Private && message.From?.Id == message.Chat.Id)
                await Send(message.Chat.Id, StorageError, ct);
        }
    }

    private async Task OnMessageCore(Message message, CancellationToken ct)
    {
        if (message.From is not { IsBot: false } user || message.Chat.Type != ChatType.Private || message.Chat.Id != user.Id)
            return;
        var key = (message.Chat.Id, user.Id);
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        if (text.StartsWith('/'))
        {
            _drafts.TryRemove(key, out _);
            _deletions.TryRemove(key, out _);
            switch (command)
            {
                case "/start":
                    await Send(key.Item1, "Добро пожаловать! Создавайте списки и задачи, отмечайте выполненные и просматривайте их по страницам.\n\n" + CommandCatalog.Help, ct);
                    break;
                case "/help": await Send(key.Item1, CommandCatalog.Help, ct); break;
                case "/show": await ShowLists(key.Item1, user.Id, null, 0, false, ct); break;
                case "/addlist":
                    _drafts[key] = new Draft { Kind = DraftKind.List, Stage = DraftStage.Name };
                    await Send(key.Item1, "Введите название списка (до 120 символов). Для отмены: /cancel.", ct);
                    break;
                case "/addtask":
                    _drafts[key] = new Draft { Kind = DraftKind.Task, Stage = DraftStage.List };
                    await ShowLists(key.Item1, user.Id, null, 0, true, ct);
                    break;
                case "/deletelist": await ShowDeleteLists(key.Item1, user.Id, ct); break;
                case "/report":
                    var all = await tasks.GetAll(user.Id, ct);
                    await Send(key.Item1, $"Всего задач: {all.Count}\nАктивные: {all.Count(t => !t.IsCompleted)}\nВыполненные: {all.Count(t => t.IsCompleted)}", ct);
                    break;
                case "/cancel": await Send(key.Item1, "Текущий диалог отменён. /show - открыть задачи.", ct); break;
                default: await Send(key.Item1, "Неизвестная команда. Откройте /help. Для выполнения и удаления задачи используйте её кнопки.", ct); break;
            }
            return;
        }
        if (!_drafts.TryGetValue(key, out var draft))
        {
            await Send(key.Item1, "Откройте /show или добавьте задачу командой /addtask. Помощь: /help.", ct);
            return;
        }
        try
        {
            if (draft.Stage == DraftStage.List)
            {
                await Send(key.Item1, "Выберите список кнопкой в предыдущем сообщении или отмените действие: /cancel.", ct);
                return;
            }
            if (draft.Stage == DraftStage.Name)
            {
                if (text.Length > 120 || text.Any(char.IsControl))
                {
                    await Send(key.Item1, "Название должно быть одной строкой длиной от 1 до 120 символов.", ct);
                    return;
                }
                if (draft.Kind == DraftKind.List)
                {
                    await lists.Add(user.Id, text, ct);
                    _drafts.TryRemove(key, out _);
                    await Send(key.Item1, "Список создан. /show - открыть списки.", ct);
                }
                else
                {
                    draft.Name = text;
                    draft.Stage = DraftStage.Date;
                    await Send(key.Item1, "Введите дату выполнения в формате YYYY-MM-DD или «-», если срок не нужен.", ct);
                }
                return;
            }
            DateTimeOffset? due = null;
            if (text != "-")
            {
                if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    await Send(key.Item1, "Не удалось распознать дату. Используйте YYYY-MM-DD или «-».", ct);
                    return;
                }
                due = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
            }
            await tasks.Add(user.Id, draft.ListId, draft.Name!, due, ct);
            _drafts.TryRemove(key, out _);
            await Send(key.Item1, "Задача создана. /show - открыть задачи.", ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _drafts.TryRemove(key, out _);
            await Send(key.Item1, "Не удалось сохранить данные: название или список недоступны. Начните действие заново.", ct);
        }
    }

    public async Task OnCallbackQuery(CallbackQuery query, CancellationToken ct)
    {
        try { await OnCallbackQueryCore(query, ct); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (query.Message is { } message && message.Chat.Type == ChatType.Private && message.Chat.Id == query.From.Id)
                await Edit(message.Chat.Id, message.Id, StorageError, Menu(), ct);
        }
    }

    private async Task OnCallbackQueryCore(CallbackQuery query, CancellationToken ct)
    {
        await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        if (query.Message is not { } message || message.Chat.Type != ChatType.Private || message.Chat.Id != query.From.Id)
            return;
        var chat = message.Chat.Id;
        var user = query.From.Id;
        var key = (chat, user);
        var id = message.Id;
        try
        {
            if (query.Data is null || System.Text.Encoding.UTF8.GetByteCount(query.Data) > 64)
                throw new FormatException("Invalid callback.");
            var action = CallbackDto.ParseAction(query.Data);
            switch (action)
            {
                case "show":
                case "show_completed":
                    await ShowTasks(chat, user, id, PagedListCallbackDto.FromString(query.Data), ct);
                    break;
                case "lists":
                case "picklists":
                    var page = PagedListCallbackDto.FromString(query.Data);
                    await ShowLists(chat, user, id, page.Page, action == "picklists", ct);
                    break;
                case "picklist":
                    var selection = ToDoListCallbackDto.FromString(query.Data);
                    if (!_drafts.TryGetValue(key, out var draft) || draft.Stage != DraftStage.List || draft.MessageId != id)
                    {
                        await Edit(chat, id, "Этот диалог уже завершён. Начните заново: /addtask.", null, ct);
                        break;
                    }
                    var list = AsNullable(selection.ToDoListId);
                    if (list is not null && await lists.Get(user, list.Value, ct) is null)
                    {
                        await Edit(chat, id, "Список больше недоступен. Начните заново: /addtask.", null, ct);
                        _drafts.TryRemove(key, out _);
                        break;
                    }
                    draft.ListId = list;
                    draft.Stage = DraftStage.Name;
                    await Edit(chat, id, "Введите название задачи (до 120 символов). Для отмены: /cancel.", null, ct);
                    break;
                case "showtask":
                    await ShowTask(chat, user, id, ToDoItemCallbackDto.FromString(query.Data).ToDoItemId, ct);
                    break;
                case "completetask":
                    var itemId = ToDoItemCallbackDto.FromString(query.Data).ToDoItemId;
                    var item = await OwnedTask(user, itemId, ct);
                    if (item is null) { await Unavailable(chat, id, ct); break; }
                    await tasks.Complete(user, item.Id, ct);
                    await ShowTask(chat, user, id, item.Id, ct);
                    break;
                case "deletetask":
                    var targetId = ToDoItemCallbackDto.FromString(query.Data).ToDoItemId;
                    if (await OwnedTask(user, targetId, ct) is null) { await Unavailable(chat, id, ct); break; }
                    await StartDelete(chat, user, id, targetId, _deleteTask, ct);
                    break;
                case "deletelist":
                    var listId = ToDoListCallbackDto.FromString(query.Data).ToDoListId;
                    if (await lists.Get(user, listId, ct) is null) { await Unavailable(chat, id, ct); break; }
                    await StartDelete(chat, user, id, listId, _deleteList, ct);
                    break;
                case "confirmdelete":
                case "canceldelete":
                    var parts = query.Data.Split('|');
                    if (parts.Length != 2 || !_deletions.TryGetValue(key, out var pending) ||
                        pending.MessageId != id ||
                        DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(10))
                    {
                        await Edit(chat, id, "Подтверждение устарело. Откройте задачу или список заново через /show.", Menu(), ct);
                        break;
                    }
                    // A mismatching token must not replace a still-valid confirmation screen.
                    if (parts[1] != pending.Nonce) break;
                    if (!_deletions.TryRemove(key, out _)) break;
                    var result = await pending.Scenario.Handle(pending.Context,
                        action == "confirmdelete" ? ScenarioInput.Confirm : ScenarioInput.Cancel, ct);
                    await Edit(chat, id, result.Text, Menu(), ct);
                    break;
                default: throw new FormatException("Unknown callback.");
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            await Edit(chat, id, "Кнопка недействительна или устарела. Откройте список заново.", Menu(), ct);
        }
    }

    private async Task ShowLists(long chat, long user, int? messageId, int page, bool select, CancellationToken ct)
    {
        if (select && (!_drafts.TryGetValue((chat, user), out var active) ||
                       active.Stage != DraftStage.List ||
                       (messageId.HasValue && active.MessageId != messageId.Value)))
        {
            if (messageId is { } stale) await Edit(chat, stale, "Начните добавление заново: /addtask.", Menu(), ct);
            return;
        }
        var all = await lists.GetAll(user, ct);
        var buttons = new List<KeyValuePair<string, string>>
        {
            new("📌Без списка", select ? new ToDoListCallbackDto("picklist", Guid.Empty).ToString() : new PagedListCallbackDto("show", Guid.Empty, 0).ToString())
        };
        buttons.AddRange(all.Select(l => new KeyValuePair<string, string>(l.Name,
            select ? new ToDoListCallbackDto("picklist", l.Id).ToString() : new PagedListCallbackDto("show", l.Id, 0).ToString())));
        var actualPage = ClampPage(page, buttons.Count);
        var markup = BuildPagedButtons(buttons, new PagedListCallbackDto(select ? "picklists" : "lists", Guid.Empty, actualPage));
        if (messageId is { } id)
        {
            await Edit(chat, id, select ? "Выберите список для новой задачи" : "Выберите список", markup, ct);
            if (select && _drafts.TryGetValue((chat, user), out var draft)) draft.MessageId = id;
        }
        else
        {
            var sent = await bot.SendMessage(chat, select ? "Выберите список для новой задачи" : "Выберите список", replyMarkup: markup, cancellationToken: ct);
            if (select && _drafts.TryGetValue((chat, user), out var draft)) draft.MessageId = sent.Id;
        }
    }

    private async Task ShowTasks(long chat, long user, int messageId, PagedListCallbackDto dto, CancellationToken ct)
    {
        var listId = AsNullable(dto.ToDoListId);
        var name = "Без списка";
        if (listId is { } real)
        {
            var list = await lists.Get(user, real, ct);
            if (list is null) { await Unavailable(chat, messageId, ct); return; }
            name = list.Name;
        }
        var completed = dto.Action == "show_completed";
        var items = await tasks.GetItems(user, listId, completed, ct);
        var page = ClampPage(dto.Page, items.Count);
        dto = new PagedListCallbackDto(dto.Action, dto.ToDoListId, page);
        _views[(chat, user)] = new View(messageId, dto);
        var buttons = items.Select(t => new KeyValuePair<string, string>(t.Name,
            new ToDoItemCallbackDto("showtask", t.Id).ToString())).ToArray();
        var rows = BuildPagedButtons(buttons, dto).InlineKeyboard.Select(r => r.ToArray()).ToList();
        rows.Add([InlineKeyboardButton.WithCallbackData(completed ? "⬜Посмотреть активные" : "☑️Посмотреть выполненные",
            new PagedListCallbackDto(completed ? "show" : "show_completed", dto.ToDoListId, 0).ToString())]);
        rows.Add([InlineKeyboardButton.WithCallbackData("К спискам", new PagedListCallbackDto("lists", Guid.Empty, 0).ToString())]);
        var title = completed ? "Выполненные задачи" : "Активные задачи";
        var text = items.Count == 0 ? "Задач нет" : $"{title} • {name}\nСтраница {page + 1} из {(items.Count - 1) / _pageSize + 1}";
        await Edit(chat, messageId, text, new InlineKeyboardMarkup(rows), ct);
    }

    private async Task ShowTask(long chat, long user, int messageId, Guid taskId, CancellationToken ct)
    {
        var task = await OwnedTask(user, taskId, ct);
        if (task is null) { await Unavailable(chat, messageId, ct); return; }
        var back = _views.TryGetValue((chat, user), out var view) && view.MessageId == messageId ? view.List :
            new PagedListCallbackDto(task.IsCompleted ? "show_completed" : "show", task.ToDoListId ?? Guid.Empty, 0);
        var rows = new List<InlineKeyboardButton[]>();
        if (!task.IsCompleted)
            rows.Add([InlineKeyboardButton.WithCallbackData("✅Выполнить", new ToDoItemCallbackDto("completetask", task.Id).ToString())]);
        rows.Add([InlineKeyboardButton.WithCallbackData("❌Удалить", new ToDoItemCallbackDto("deletetask", task.Id).ToString())]);
        rows.Add([InlineKeyboardButton.WithCallbackData("К задачам", back.ToString())]);
        var due = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "не указан";
        var completion = task.CompletedAt is { } completedAt
            ? $"\nВремя выполнения: {completedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC"
            : string.Empty;
        await Edit(chat, messageId,
            $"{task.Name}\n\nСтатус: {(task.IsCompleted ? "Выполнена" : "Активна")}\nСрок выполнения: {due}\nСоздана: {task.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC{completion}",
            new InlineKeyboardMarkup(rows), ct);
    }

    private InlineKeyboardMarkup BuildPagedButtons(IReadOnlyList<KeyValuePair<string, string>> callbackData, PagedListCallbackDto listDto)
    {
        var totalPages = callbackData.Count == 0 ? 0 : (callbackData.Count - 1) / _pageSize + 1;
        var rows = callbackData.GetBatchByNumber(_pageSize, listDto.Page)
            .Select(pair => new[] { InlineKeyboardButton.WithCallbackData(pair.Key, pair.Value) }).ToList();
        var arrows = new List<InlineKeyboardButton>();
        if (listDto.Page > 0)
            arrows.Add(InlineKeyboardButton.WithCallbackData("⬅️", new PagedListCallbackDto(listDto.Action, listDto.ToDoListId, listDto.Page - 1).ToString()));
        if (listDto.Page < totalPages - 1)
            arrows.Add(InlineKeyboardButton.WithCallbackData("➡️", new PagedListCallbackDto(listDto.Action, listDto.ToDoListId, listDto.Page + 1).ToString()));
        if (arrows.Count > 0) rows.Add(arrows.ToArray());
        return new InlineKeyboardMarkup(rows);
    }

    private async Task ShowDeleteLists(long chat, long user, CancellationToken ct)
    {
        var all = await lists.GetAll(user, ct);
        if (all.Count == 0) { await Send(chat, "Списков для удаления нет.", ct); return; }
        var rows = all.Select(l => new[] { InlineKeyboardButton.WithCallbackData(l.Name, new ToDoListCallbackDto("deletelist", l.Id).ToString()) });
        await bot.SendMessage(chat, "Выберите список для удаления. Задачи будут сохранены в «Без списка».", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task StartDelete(long chat, long user, int messageId, Guid target, IScenario scenario, CancellationToken ct)
    {
        var context = new ScenarioContext(user, target);
        var result = await scenario.Handle(context, ScenarioInput.Start, ct);
        if (!result.AwaitingConfirmation) { await Edit(chat, messageId, result.Text, Menu(), ct); return; }
        var nonce = Guid.NewGuid().ToString("N");
        _deletions[(chat, user)] = new PendingDelete(messageId, nonce, context, scenario, DateTimeOffset.UtcNow);
        await Edit(chat, messageId, result.Text, new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("Да, удалить", $"confirmdelete|{nonce}")],
            [InlineKeyboardButton.WithCallbackData("Отмена", $"canceldelete|{nonce}")]
        ]), ct, preserveConfirmation: true);
    }

    private async Task<ToDoItem?> OwnedTask(long user, Guid id, CancellationToken ct)
    {
        var task = await tasks.Get(id, ct);
        return task?.UserId == user ? task : null;
    }

    private Task Unavailable(long chat, int messageId, CancellationToken ct) =>
        Edit(chat, messageId, "Задача или список не найдены либо недоступны.", Menu(), ct);

    private static InlineKeyboardMarkup Menu() => new(InlineKeyboardButton.WithCallbackData("К спискам", new PagedListCallbackDto("lists", Guid.Empty, 0).ToString()));
    private const string StorageError = "Не удалось прочитать или сохранить данные. Файл данных не перезаписан. Проверьте доступ к хранилищу и его целостность, затем повторите действие.";
    private static Guid? AsNullable(Guid id) => id == Guid.Empty ? null : id;
    private static int ClampPage(int page, int count) => Math.Clamp(page, 0, count == 0 ? 0 : (count - 1) / _pageSize);
    private async Task Send(long chat, string text, CancellationToken ct) => await bot.SendMessage(chat, text, cancellationToken: ct);

    private async Task Edit(long chat, int messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct,
        bool preserveConfirmation = false)
    {
        // Only private chats reach this method, so the chat ID also identifies the user.
        var key = (chat, chat);
        if (!preserveConfirmation && _deletions.TryGetValue(key, out var pending) && pending.MessageId == messageId)
            _deletions.TryRemove(key, out _);
        try { await bot.EditMessageText(chat, messageId, text, replyMarkup: markup, cancellationToken: ct); }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { }
    }

    private enum DraftKind { Task, List }
    private enum DraftStage { List, Name, Date }
    private sealed class Draft
    {
        public DraftKind Kind { get; init; }
        public DraftStage Stage { get; set; }
        public int MessageId { get; set; }
        public Guid? ListId { get; set; }
        public string? Name { get; set; }
    }
    private sealed record View(int MessageId, PagedListCallbackDto List);
    private sealed record PendingDelete(int MessageId, string Nonce, ScenarioContext Context, IScenario Scenario, DateTimeOffset CreatedAt);
}
