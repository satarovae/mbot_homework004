using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using ToDoBot.Callbacks;
using ToDoBot.Handlers;
using ToDoBot.Models;
using ToDoBot.Services;
using Xunit;

namespace ToDoBot.Tests;

public sealed class UpdateHandlerTests : IDisposable
{
    private const long Owner = 50101;
    private const long OtherUser = 60202;
    private const int MenuMessageId = 77;
    private readonly string _directory;
    private readonly TestTelegramHttpHandler _transport = new();
    private readonly HttpClient _http;
    private readonly TelegramBotClient _bot;
    private readonly UpdateHandler _handler;
    private readonly IToDoService _tasks;
    private readonly IToDoListService _lists;

    public UpdateHandlerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ToDoBot.HandlerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var storage = new JsonToDoService(Path.Combine(_directory, "todos.json"));
        _tasks = storage;
        _lists = storage;
        _http = new HttpClient(_transport);
        _bot = new TelegramBotClient("123456789:OFFLINE_TEST_TOKEN_NOT_A_REAL_SECRET", _http);
        _handler = new UpdateHandler(_bot, _tasks, _lists);
    }

    [Fact]
    public async Task HelpAndRegisteredCatalogExposeOnlyCurrentCommands()
    {
        var expected = new[] { "start", "help", "show", "addtask", "addlist", "deletelist", "report", "cancel" };
        Assert.Equal(expected, CommandCatalog.Commands.Select(command => command.Command));
        await _bot.SetMyCommands(CommandCatalog.Commands, cancellationToken: CancellationToken.None);
        var registration = Assert.Single(Requests("setMyCommands"));
        Assert.Equal(expected, registration.Body.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("command").GetString()));

        await Message("/help");

        var help = Assert.Single(Requests("sendMessage")).Text;
        var helpCommands = Regex.Matches(help, @"(?m)^/([a-z]+)")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "show", "addtask", "addlist", "deletelist", "report", "cancel", "help" }, helpCommands);
        Assert.Contains("пяти задач", help);
        Assert.DoesNotContain("completetask", helpCommands);
        Assert.DoesNotContain("deletetask", helpCommands);
    }

    [Theory]
    [InlineData("/completetask")]
    [InlineData("/removetask")]
    [InlineData("/deletetask")]
    [InlineData("/complete")]
    [InlineData("/delete")]
    public async Task ObsoleteTextCommandsAreRejectedWithoutChangingTasks(string command)
    {
        var task = await _tasks.Add(Owner, null, "Сохранить", null, CancellationToken.None);

        await Message($"{command} {task.Id}");

        Assert.Contains("Неизвестная команда", LastSend.Text);
        Assert.Equal(task, await _tasks.Get(task.Id, CancellationToken.None));
        Assert.Empty(Requests("editMessageText"));
    }

    [Fact]
    public async Task TwelveTasksArePagedFiveAtATimeWithCorrectBoundaryArrows()
    {
        var (list, items) = await SeedTasks(12);

        await Callback(new PagedListCallbackDto("show", list.Id, 0).ToString());
        AssertPage(LastEdit, items.Take(5), "Страница 1 из 3");
        Assert.Null(Arrow(LastEdit, "⬅️"));
        var next = Assert.IsType<TestTelegramButton>(Arrow(LastEdit, "➡️"));
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 1).ToString(), next.Data);

        await Callback(next.Data);
        AssertPage(LastEdit, items.Skip(5).Take(5), "Страница 2 из 3");
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 0).ToString(), Arrow(LastEdit, "⬅️")!.Data);
        next = Assert.IsType<TestTelegramButton>(Arrow(LastEdit, "➡️"));
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 2).ToString(), next.Data);

        await Callback(next.Data);
        AssertPage(LastEdit, items.Skip(10), "Страница 3 из 3");
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 1).ToString(), Arrow(LastEdit, "⬅️")!.Data);
        Assert.Null(Arrow(LastEdit, "➡️"));
        Assert.Equal(3, Requests("answerCallbackQuery").Count);
        Assert.Empty(Requests("sendMessage"));
        Assert.All(Requests("editMessageText"), request =>
            Assert.Equal(MenuMessageId, request.Body.GetProperty("message_id").GetInt32()));
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, false, 1)]
    [InlineData(5, false, 1)]
    [InlineData(6, false, 2)]
    [InlineData(10, false, 2)]
    [InlineData(11, false, 3)]
    [InlineData(0, true, 1)]
    [InlineData(1, true, 1)]
    [InlineData(5, true, 1)]
    [InlineData(6, true, 2)]
    [InlineData(10, true, 2)]
    [InlineData(11, true, 3)]
    public async Task ActiveAndCompletedPageBoundariesHaveFiveButtonsAndCorrectArrows(
        int count, bool completed, int expectedPages)
    {
        var (list, matchingTasks) = await SeedTasks(count, completed);
        var opposite = await _tasks.Add(Owner, list.Id, "Другая группа статуса", null, CancellationToken.None);
        if (!completed)
            await _tasks.Complete(Owner, opposite.Id, CancellationToken.None);
        var action = completed ? "show_completed" : "show";

        for (var page = 0; page < expectedPages; page++)
        {
            await Callback(new PagedListCallbackDto(action, list.Id, page).ToString());

            if (count == 0)
            {
                Assert.Equal("Задач нет", LastEdit.Text);
                Assert.Empty(TaskButtons(LastEdit));
            }
            else
            {
                AssertPage(LastEdit, matchingTasks.Skip(page * 5).Take(5),
                    $"Страница {page + 1} из {expectedPages}");
            }
            Assert.DoesNotContain(TaskButtons(LastEdit), button => button.Text == opposite.Name);
            Assert.Equal(MenuMessageId, LastEdit.Body.GetProperty("message_id").GetInt32());
            if (page == 0)
                Assert.Null(Arrow(LastEdit, "⬅️"));
            else
                Assert.Equal(new PagedListCallbackDto(action, list.Id, page - 1).ToString(), Arrow(LastEdit, "⬅️")!.Data);
            if (page == expectedPages - 1)
                Assert.Null(Arrow(LastEdit, "➡️"));
            else
                Assert.Equal(new PagedListCallbackDto(action, list.Id, page + 1).ToString(), Arrow(LastEdit, "➡️")!.Data);
        }

        Assert.Equal(expectedPages, Requests("answerCallbackQuery").Count);
        Assert.Equal(expectedPages, Requests("editMessageText").Count);
        Assert.Empty(Requests("sendMessage"));
    }

    [Fact]
    public async Task ListMenuIncludesVirtualListAndUsesItsOwnPagedCallbacks()
    {
        for (var i = 1; i <= 12; i++)
            await _lists.Add(Owner, $"Список {i:00}", CancellationToken.None);
        await _lists.Add(OtherUser, "Чужой список", CancellationToken.None);

        await Message("/show");

        var first = LastSend;
        var listButtons = first.Buttons.Where(button => button.Data.StartsWith("show|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, listButtons.Length);
        Assert.Equal("📌Без списка", listButtons[0].Text);
        Assert.Equal(new PagedListCallbackDto("show", Guid.Empty, 0).ToString(), listButtons[0].Data);
        Assert.DoesNotContain(first.Buttons, button => button.Text == "Чужой список");
        Assert.Null(Arrow(first, "⬅️"));
        var next = Arrow(first, "➡️")!;
        var dto = PagedListCallbackDto.FromString(next.Data);
        Assert.Equal("lists", dto.Action);
        Assert.Equal(1, dto.Page);

        await Callback(next.Data, messageId: _transport.LastSentMessageId);
        Assert.Equal(5, LastEdit.Buttons.Count(button => button.Data.StartsWith("show|", StringComparison.Ordinal)));
        next = Arrow(LastEdit, "➡️")!;
        await Callback(next.Data, messageId: _transport.LastSentMessageId);
        Assert.Equal(3, LastEdit.Buttons.Count(button => button.Data.StartsWith("show|", StringComparison.Ordinal)));
        Assert.Null(Arrow(LastEdit, "➡️"));
        Assert.Equal(1, PagedListCallbackDto.FromString(Arrow(LastEdit, "⬅️")!.Data).Page);
    }

    [Fact]
    public async Task CompletedPaginationKeepsItsFilterAndEmptyViewHasNoArrows()
    {
        var (list, completed) = await SeedTasks(12, completed: true);
        var active = await _tasks.Add(Owner, list.Id, "Только активная", null, CancellationToken.None);

        await Callback(new PagedListCallbackDto("show_completed", list.Id, 0).ToString());

        AssertPage(LastEdit, completed.Take(5), "Страница 1 из 3");
        Assert.Contains("Выполненные задачи", LastEdit.Text);
        Assert.Equal("show_completed", PagedListCallbackDto.FromString(Arrow(LastEdit, "➡️")!.Data).Action);
        var activeToggle = Assert.Single(LastEdit.Buttons, button => button.Text.Contains("Посмотреть активные", StringComparison.Ordinal));
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 0).ToString(), activeToggle.Data);

        await Callback(new PagedListCallbackDto("show_completed", list.Id, 2).ToString());
        AssertPage(LastEdit, completed.Skip(10), "Страница 3 из 3");
        Assert.Equal("show_completed", PagedListCallbackDto.FromString(Arrow(LastEdit, "⬅️")!.Data).Action);
        Assert.Null(Arrow(LastEdit, "➡️"));

        await Callback(activeToggle.Data);
        AssertPage(LastEdit, [active], "Страница 1 из 1");
        var emptyList = await _lists.Add(Owner, "Пустой список", CancellationToken.None);
        await Callback(new PagedListCallbackDto("show_completed", emptyList.Id, 99).ToString());
        Assert.Equal("Задач нет", LastEdit.Text);
        Assert.Empty(TaskButtons(LastEdit));
        Assert.Null(Arrow(LastEdit, "⬅️"));
        Assert.Null(Arrow(LastEdit, "➡️"));
        Assert.Contains(LastEdit.Buttons, button => button.Text.Contains("Посмотреть активные", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingTheLastTaskOnLastPageClampsAStalePageToTheNewLastPage()
    {
        var (list, items) = await SeedTasks(11);
        await Callback(new PagedListCallbackDto("show", list.Id, 2).ToString());
        AssertPage(LastEdit, items.Skip(10), "Страница 3 из 3");
        var removed = items[10];

        await Callback(new ToDoItemCallbackDto("deletetask", removed.Id).ToString());
        var confirmation = Button(LastEdit, "Да, удалить").Data;
        await Callback(confirmation);
        Assert.Null(await _tasks.Get(removed.Id, CancellationToken.None));
        await Callback(new PagedListCallbackDto("show", list.Id, 2).ToString());

        AssertPage(LastEdit, items.Skip(5).Take(5), "Страница 2 из 2");
        Assert.Equal(0, PagedListCallbackDto.FromString(Arrow(LastEdit, "⬅️")!.Data).Page);
        Assert.Null(Arrow(LastEdit, "➡️"));
    }

    [Fact]
    public async Task ListToTaskNavigationEditsTheSameMessageAndRetainsTheBackPage()
    {
        var (list, items) = await SeedTasks(8);
        await Callback(new PagedListCallbackDto("lists", Guid.Empty, 0).ToString());
        var listButton = Assert.Single(LastEdit.Buttons, button => button.Text == list.Name);
        await Callback(listButton.Data);
        await Callback(Arrow(LastEdit, "➡️")!.Data);
        var taskButton = TaskButtons(LastEdit)[1];
        var selectedId = ToDoItemCallbackDto.FromString(taskButton.Data).ToDoItemId;

        await Callback(taskButton.Data);

        Assert.Contains(items.Single(item => item.Id == selectedId).Name, LastEdit.Text);
        Assert.Contains("Статус: Активна", LastEdit.Text);
        Assert.Contains("Срок выполнения: не указан", LastEdit.Text);
        Assert.Equal(new PagedListCallbackDto("show", list.Id, 1).ToString(), Button(LastEdit, "К задачам").Data);
        Assert.Empty(Requests("sendMessage"));
        Assert.All(Requests("editMessageText"), request =>
            Assert.Equal(MenuMessageId, request.Body.GetProperty("message_id").GetInt32()));
    }

    [Fact]
    public async Task CompletingTaskChecksOwnershipAndEditsTheDetailWithoutSendingANewMessage()
    {
        var own = await _tasks.Add(Owner, null, "Своя задача", null, CancellationToken.None);
        var foreign = await _tasks.Add(OtherUser, null, "Чужая задача", null, CancellationToken.None);

        await Callback(new ToDoItemCallbackDto("completetask", foreign.Id).ToString());
        Assert.Contains("недоступны", LastEdit.Text);
        Assert.False((await _tasks.Get(foreign.Id, CancellationToken.None))!.IsCompleted);
        Assert.DoesNotContain(foreign.Name, LastEdit.Text);

        await Callback(new ToDoItemCallbackDto("completetask", own.Id).ToString());
        Assert.True((await _tasks.Get(own.Id, CancellationToken.None))!.IsCompleted);
        Assert.Contains("Статус: Выполнена", LastEdit.Text);
        Assert.DoesNotContain(LastEdit.Buttons, button => button.Data.StartsWith("completetask|", StringComparison.Ordinal));
        Assert.Contains(LastEdit.Buttons, button => button.Data.StartsWith("deletetask|", StringComparison.Ordinal));
        Assert.Empty(Requests("sendMessage"));
    }

    [Fact]
    public async Task CompletedTaskCardShowsItsCompletionTimestampInUtcAndCannotBeCompletedAgain()
    {
        var task = await _tasks.Add(Owner, null, "Выполненная с датой", null, CancellationToken.None);
        await _tasks.Complete(Owner, task.Id, CancellationToken.None);
        var filePath = Path.Combine(_directory, "todos.json");
        var data = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(filePath))!;
        var savedItem = Assert.Single(data["items"]!.AsArray())!;
        savedItem["createdAt"] = "2024-01-01T00:00:00+00:00";
        savedItem["completedAt"] = "2024-04-02T13:45:59+07:00";
        await System.IO.File.WriteAllTextAsync(filePath, data.ToJsonString());
        var completedAt = new DateTimeOffset(2024, 4, 2, 13, 45, 59, TimeSpan.FromHours(7));

        await Callback(new ToDoItemCallbackDto("showtask", task.Id).ToString());

        Assert.Contains($"Время выполнения: {completedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", LastEdit.Text);
        Assert.Contains("Статус: Выполнена", LastEdit.Text);
        Assert.DoesNotContain(LastEdit.Buttons, button => button.Data.StartsWith("completetask|", StringComparison.Ordinal));
        Assert.Equal(new PagedListCallbackDto("show_completed", Guid.Empty, 0).ToString(), Button(LastEdit, "К задачам").Data);
        Assert.Empty(Requests("sendMessage"));
    }

    [Fact]
    public async Task DeleteRequiresConfirmationCancelAndConfirmationTokensCannotBeReplayed()
    {
        var task = await _tasks.Add(Owner, null, "Сначала спросить", null, CancellationToken.None);
        await Callback(new ToDoItemCallbackDto("deletetask", task.Id).ToString());

        Assert.Equal(task, await _tasks.Get(task.Id, CancellationToken.None));
        Assert.Contains("Удалить задачу", LastEdit.Text);
        var firstConfirm = Button(LastEdit, "Да, удалить").Data;
        var firstCancel = Button(LastEdit, "Отмена").Data;
        Assert.Equal(firstConfirm.Split('|')[1], firstCancel.Split('|')[1]);
        Assert.InRange(Encoding.UTF8.GetByteCount(firstConfirm), 1, 64);
        await Callback(firstCancel);
        Assert.Contains("отменено", LastEdit.Text);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));

        await Callback(firstConfirm);
        Assert.Contains("устарело", LastEdit.Text);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));
        await Callback(new ToDoItemCallbackDto("deletetask", task.Id).ToString());
        var secondConfirm = Button(LastEdit, "Да, удалить").Data;
        Assert.NotEqual(firstConfirm, secondConfirm);
        await Callback(firstConfirm);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));
        await Callback(secondConfirm);
        Assert.Contains("Задача удалена", LastEdit.Text);
        Assert.Null(await _tasks.Get(task.Id, CancellationToken.None));
        await Callback(secondConfirm);
        Assert.Contains("устарело", LastEdit.Text);
        Assert.Null(await _tasks.Get(task.Id, CancellationToken.None));
        Assert.Empty(Requests("sendMessage"));
    }

    [Fact]
    public async Task ConfirmationRejectsWrongNonceWrongMessageAndOtherUsersWithoutConsumingTheValidToken()
    {
        var task = await _tasks.Add(Owner, null, "Не удалить чужим нажатием", null, CancellationToken.None);
        await Callback(new ToDoItemCallbackDto("deletetask", task.Id).ToString());
        var confirmation = Button(LastEdit, "Да, удалить").Data;
        var editsBeforeWrongNonce = Requests("editMessageText").Count;

        await Callback("confirmdelete|" + new string('0', 32));
        Assert.Equal(editsBeforeWrongNonce, Requests("editMessageText").Count);
        Assert.Contains("Удалить задачу", LastEdit.Text);
        Assert.Equal(confirmation, Button(LastEdit, "Да, удалить").Data);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));
        await Callback(confirmation, messageId: MenuMessageId + 1);
        Assert.Contains("устарело", LastEdit.Text);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));
        await Callback(confirmation, userId: OtherUser);
        Assert.Contains("устарело", LastEdit.Text);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));
        var editsBeforeCrossChat = Requests("editMessageText").Count;
        await Callback(confirmation, userId: OtherUser, chatId: Owner);
        Assert.Equal(editsBeforeCrossChat, Requests("editMessageText").Count);
        Assert.NotNull(await _tasks.Get(task.Id, CancellationToken.None));

        await Callback(confirmation);
        Assert.Null(await _tasks.Get(task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmFromScreenReplacedByNavigationIsRejected()
    {
        var first = await _tasks.Add(Owner, null, "Задача A", null, CancellationToken.None);
        var second = await _tasks.Add(Owner, null, "Задача B", null, CancellationToken.None);
        await Callback(new ToDoItemCallbackDto("deletetask", first.Id).ToString());
        var staleConfirmation = Button(LastEdit, "Да, удалить").Data;
        await Callback(new ToDoItemCallbackDto("showtask", second.Id).ToString());
        Assert.Contains(second.Name, LastEdit.Text);
        Assert.DoesNotContain(LastEdit.Buttons, button => button.Data.StartsWith("confirmdelete|", StringComparison.Ordinal));

        await Callback(staleConfirmation);

        Assert.Equal(first, await _tasks.Get(first.Id, CancellationToken.None));
        Assert.Equal(second, await _tasks.Get(second.Id, CancellationToken.None));
        Assert.Contains("устарело", LastEdit.Text);
    }

    [Fact]
    public async Task ForeignTaskAndListDeletionRequestsDoNotOfferAConfirmation()
    {
        var foreignList = await _lists.Add(OtherUser, "Чужой список", CancellationToken.None);
        var task = await _tasks.Add(OtherUser, foreignList.Id, "Чужая задача", null, CancellationToken.None);

        await Callback(new ToDoItemCallbackDto("deletetask", task.Id).ToString());
        Assert.Contains("недоступны", LastEdit.Text);
        Assert.DoesNotContain(LastEdit.Buttons, button => button.Data.StartsWith("confirmdelete|", StringComparison.Ordinal));
        await Callback(new ToDoListCallbackDto("deletelist", foreignList.Id).ToString());
        Assert.Contains("недоступны", LastEdit.Text);
        Assert.DoesNotContain(LastEdit.Buttons, button => button.Data.StartsWith("confirmdelete|", StringComparison.Ordinal));
        Assert.Equal(task, await _tasks.Get(task.Id, CancellationToken.None));
        Assert.Equal(foreignList, await _lists.Get(OtherUser, foreignList.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmingListDeletionKeepsItsTasksAndMovesThemToTheVirtualList()
    {
        var (list, items) = await SeedTasks(2);
        await Callback(new ToDoListCallbackDto("deletelist", list.Id).ToString());
        Assert.NotNull(await _lists.Get(Owner, list.Id, CancellationToken.None));
        Assert.All(await _tasks.GetAll(Owner, CancellationToken.None), task => Assert.Equal(list.Id, task.ToDoListId));
        var confirmation = Button(LastEdit, "Да, удалить").Data;

        await Callback(confirmation);

        Assert.Null(await _lists.Get(Owner, list.Id, CancellationToken.None));
        Assert.Contains("Без списка", LastEdit.Text);
        var remaining = await _tasks.GetItems(Owner, null, false, CancellationToken.None);
        Assert.Equal(items.Select(task => task.Id), remaining.Select(task => task.Id));
        Assert.All(remaining, task => Assert.Null(task.ToDoListId));
    }

    public static TheoryData<string?> InvalidCallbacks => new()
    {
        null,
        "",
        "unknown|value",
        "show",
        "show|not-a-guid|0",
        "show|00000000000000000000000000000000|-1",
        "show|00000000000000000000000000000000|2147483648",
        new string('я', 33)
    };

    [Theory]
    [MemberData(nameof(InvalidCallbacks))]
    public async Task InvalidCallbacksAreAcknowledgedAndDoNotThrowOrChangeStoredTasks(string? data)
    {
        var task = await _tasks.Add(Owner, null, "Не менять", null, CancellationToken.None);

        await Callback(data);

        Assert.Single(Requests("answerCallbackQuery"));
        Assert.Contains("недействительна", LastEdit.Text);
        Assert.Equal(task, await _tasks.Get(task.Id, CancellationToken.None));
        Assert.Empty(Requests("sendMessage"));
    }

    [Fact]
    public async Task MessagesFromBotsGroupsOrMismatchedPrivateChatsAreIgnored()
    {
        await _handler.OnMessage(new Message
        {
            Id = 1,
            From = new User { Id = Owner, IsBot = true, FirstName = "Another bot" },
            Chat = new Chat { Id = Owner, Type = ChatType.Private },
            Text = "/show"
        }, CancellationToken.None);
        await _handler.OnMessage(new Message
        {
            Id = 2,
            From = User(Owner),
            Chat = new Chat { Id = -98765, Type = ChatType.Group },
            Text = "/show"
        }, CancellationToken.None);
        await _handler.OnMessage(new Message
        {
            Id = 3,
            From = User(Owner),
            Chat = new Chat { Id = OtherUser, Type = ChatType.Private },
            Text = "/show"
        }, CancellationToken.None);

        Assert.Empty(_transport.Requests);
    }

    [Fact]
    public async Task PollingUpdatesDispatchMessagesAndCallbacksAndIgnoreUnrelatedUpdates()
    {
        await _handler.HandleUpdateAsync(_bot, new Update
        {
            Id = 1,
            Message = new Message
            {
                Id = 9001,
                From = User(Owner),
                Chat = new Chat { Id = Owner, Type = ChatType.Private },
                Text = "/help"
            }
        }, CancellationToken.None);
        Assert.Single(Requests("sendMessage"));

        await _handler.HandleUpdateAsync(_bot, new Update
        {
            Id = 2,
            CallbackQuery = new CallbackQuery
            {
                Id = "offline-dispatch",
                From = User(Owner),
                ChatInstance = "offline-test",
                Data = new PagedListCallbackDto("show", Guid.Empty, 0).ToString(),
                Message = new Message
                {
                    Id = MenuMessageId,
                    Chat = new Chat { Id = Owner, Type = ChatType.Private }
                }
            }
        }, CancellationToken.None);
        Assert.Single(Requests("answerCallbackQuery"));
        Assert.Equal("Задач нет", Assert.Single(Requests("editMessageText")).Text);
        var requestCount = _transport.Requests.Count;

        await _handler.HandleUpdateAsync(_bot, new Update { Id = 3 }, CancellationToken.None);

        Assert.Equal(requestCount, _transport.Requests.Count);
    }

    [Fact]
    public async Task TaskCreationDialogSelectsListCollectsNameAndPersistsAValidDate()
    {
        var list = await _lists.Add(Owner, "Учёба", CancellationToken.None);
        await Message("/addtask");
        var promptId = _transport.LastSentMessageId;
        var selection = Assert.Single(LastSend.Buttons, button => button.Text == list.Name);
        await Callback(selection.Data, messageId: promptId);
        Assert.Contains("Введите название задачи", LastEdit.Text);
        Assert.Equal(promptId, LastEdit.Body.GetProperty("message_id").GetInt32());
        await Message("Подготовить отчёт");
        Assert.Contains("YYYY-MM-DD", LastSend.Text);
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));

        await Message("2030-06-30");

        var saved = Assert.Single(await _tasks.GetAll(Owner, CancellationToken.None));
        Assert.Equal("Подготовить отчёт", saved.Name);
        Assert.Equal(list.Id, saved.ToDoListId);
        Assert.Equal(new DateTimeOffset(2030, 6, 30, 0, 0, 0, TimeSpan.Zero), saved.DueDate);
        Assert.False(saved.IsCompleted);
        Assert.Contains("Задача создана", LastSend.Text);
    }

    [Fact]
    public async Task InvalidNamesAndDatesCanBeRetriedWithoutCreatingPartialTasks()
    {
        await Message("/addtask");
        await Callback(Button(LastSend, "📌Без списка").Data, messageId: _transport.LastSentMessageId);
        await Message(new string('я', 121));
        Assert.Contains("от 1 до 120", LastSend.Text);
        await Message("Первая строка\nВторая строка");
        Assert.Contains("одной строкой", LastSend.Text);
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));
        await Message("Корректное имя");
        await Message("30/06/2030");
        Assert.Contains("Не удалось распознать дату", LastSend.Text);
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));

        await Message("-");

        var saved = Assert.Single(await _tasks.GetAll(Owner, CancellationToken.None));
        Assert.Equal("Корректное имя", saved.Name);
        Assert.Null(saved.DueDate);
        Assert.Null(saved.ToDoListId);
    }

    [Fact]
    public async Task CancelingATaskDraftDiscardsItAndItsOldSelectionButtonsCannotResumeIt()
    {
        await Message("/addtask");
        var selection = Button(LastSend, "📌Без списка").Data;
        var promptId = _transport.LastSentMessageId;
        await Message("/cancel");
        await Callback(selection, messageId: promptId);
        Assert.Contains("диалог уже завершён", LastEdit.Text);
        await Message("Не сохранять");
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));

        await Message("/addtask");
        selection = Button(LastSend, "📌Без списка").Data;
        await Callback(selection, messageId: _transport.LastSentMessageId);
        await Message("Черновик до даты");
        await Message("/cancel");
        await Message("2030-06-30");
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task OldPickerPageCannotHijackANewTaskCreationDialogAfterCancellation()
    {
        var selectedList = await _lists.Add(Owner, "Список нового диалога", CancellationToken.None);
        for (var index = 1; index <= 5; index++)
            await _lists.Add(Owner, $"Дополнительный список {index}", CancellationToken.None);
        await Message("/addtask");
        var oldMessageId = _transport.LastSentMessageId;
        var oldPageCallback = Arrow(LastSend, "➡️")!.Data;
        Assert.Equal("picklists", PagedListCallbackDto.FromString(oldPageCallback).Action);
        await Message("/cancel");
        await Message("/addtask");
        var currentMessageId = _transport.LastSentMessageId;
        var currentListCallback = Button(LastSend, selectedList.Name).Data;
        Assert.NotEqual(oldMessageId, currentMessageId);

        await Callback(oldPageCallback, messageId: oldMessageId);
        await Callback(currentListCallback, messageId: currentMessageId);

        Assert.Contains("Введите название задачи", LastEdit.Text);
        Assert.Equal(currentMessageId, LastEdit.Body.GetProperty("message_id").GetInt32());
        await Message("Задача из нового диалога");
        await Message("-");
        var saved = Assert.Single(await _tasks.GetAll(Owner, CancellationToken.None));
        Assert.Equal(selectedList.Id, saved.ToDoListId);
        Assert.Equal("Задача из нового диалога", saved.Name);
    }

    [Fact]
    public async Task TaskCreationRejectsAListOwnedByAnotherUser()
    {
        var foreignList = await _lists.Add(OtherUser, "Чужой", CancellationToken.None);
        await Message("/addtask");
        var promptId = _transport.LastSentMessageId;

        await Callback(new ToDoListCallbackDto("picklist", foreignList.Id).ToString(), messageId: promptId);

        Assert.Contains("Список больше недоступен", LastEdit.Text);
        await Message("Не сохранить");
        await Message("-");
        Assert.Empty(await _tasks.GetAll(Owner, CancellationToken.None));
        Assert.Equal(foreignList, await _lists.Get(OtherUser, foreignList.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListCreationAndReportUseTheCurrentUsersData()
    {
        await Message("/addlist");
        await Message("  Работа  ");
        var list = Assert.Single(await _lists.GetAll(Owner, CancellationToken.None));
        Assert.Equal("Работа", list.Name);
        await _tasks.Add(Owner, list.Id, "Активная", null, CancellationToken.None);
        var done = await _tasks.Add(Owner, null, "Готовая", null, CancellationToken.None);
        await _tasks.Complete(Owner, done.Id, CancellationToken.None);
        await _tasks.Add(OtherUser, null, "Не учитывать", null, CancellationToken.None);

        await Message("/report");

        Assert.Equal("Всего задач: 2\nАктивные: 1\nВыполненные: 1", LastSend.Text);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":1,\"lists\":null,\"items\":[]}")]
    public async Task CorruptedStorageShowCommandSendsAReadableErrorAndPreservesTheFile(string invalidJson)
    {
        var filePath = Path.Combine(_directory, "todos.json");
        var originalBytes = Encoding.UTF8.GetBytes(invalidJson);
        await System.IO.File.WriteAllBytesAsync(filePath, originalBytes);

        await Message("/show");

        var error = Assert.Single(Requests("sendMessage"));
        Assert.Contains("Не удалось прочитать или сохранить данные", error.Text);
        Assert.Contains("Файл данных не перезаписан", error.Text);
        Assert.DoesNotContain(filePath, error.Text);
        Assert.Empty(Requests("editMessageText"));
        Assert.Equal(originalBytes, await System.IO.File.ReadAllBytesAsync(filePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"version\":1,\"lists\":null,\"items\":[]}")]
    public async Task CorruptedStorageCallbackEditsItsOwnMessageWithoutSendingOrOverwriting(string invalidJson)
    {
        var filePath = Path.Combine(_directory, "todos.json");
        var originalBytes = Encoding.UTF8.GetBytes(invalidJson);
        await System.IO.File.WriteAllBytesAsync(filePath, originalBytes);

        await Callback(new PagedListCallbackDto("show", Guid.Empty, 0).ToString());

        Assert.Single(Requests("answerCallbackQuery"));
        var error = Assert.Single(Requests("editMessageText"));
        Assert.Equal(MenuMessageId, error.Body.GetProperty("message_id").GetInt32());
        Assert.Contains("Не удалось прочитать или сохранить данные", error.Text);
        Assert.Contains("Файл данных не перезаписан", error.Text);
        Assert.DoesNotContain(filePath, error.Text);
        Assert.Empty(Requests("sendMessage"));
        Assert.Equal(originalBytes, await System.IO.File.ReadAllBytesAsync(filePath));
    }

    private async Task<(ToDoList List, IReadOnlyList<ToDoItem> Items)> SeedTasks(int count, bool completed = false)
    {
        var list = await _lists.Add(Owner, "Проверяемый список", CancellationToken.None);
        for (var i = 1; i <= count; i++)
        {
            var item = await _tasks.Add(Owner, list.Id, $"Задача {i:00}", null, CancellationToken.None);
            if (completed)
                await _tasks.Complete(Owner, item.Id, CancellationToken.None);
        }
        return (list, await _tasks.GetItems(Owner, list.Id, completed, CancellationToken.None));
    }

    private Task Message(string text, long userId = Owner) => _handler.OnMessage(new Message
    {
        Id = 9000,
        Date = DateTime.UtcNow,
        From = User(userId),
        Chat = new Chat { Id = userId, Type = ChatType.Private },
        Text = text
    }, CancellationToken.None);

    private Task Callback(string? data, long userId = Owner, long? chatId = null, int messageId = MenuMessageId) =>
        _handler.OnCallbackQuery(new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"),
            From = User(userId),
            ChatInstance = "offline-test",
            Data = data,
            Message = new Message
            {
                Id = messageId,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = chatId ?? userId, Type = ChatType.Private },
                Text = "Previous bot message"
            }
        }, CancellationToken.None);

    private static User User(long id) => new() { Id = id, IsBot = false, FirstName = "Offline tester" };
    private IReadOnlyList<TestTelegramRequest> Requests(string method) => _transport.Requests.Where(request => request.Method == method).ToArray();
    private TestTelegramRequest LastEdit => _transport.Requests.Last(request => request.Method == "editMessageText");
    private TestTelegramRequest LastSend => _transport.Requests.Last(request => request.Method == "sendMessage");
    private static TestTelegramButton Button(TestTelegramRequest request, string text) => Assert.Single(request.Buttons, button => button.Text == text);
    private static TestTelegramButton? Arrow(TestTelegramRequest request, string text) => request.Buttons.SingleOrDefault(button => button.Text == text);
    private static IReadOnlyList<TestTelegramButton> TaskButtons(TestTelegramRequest request) => request.Buttons.Where(button => button.Data.StartsWith("showtask|", StringComparison.Ordinal)).ToArray();

    private static void AssertPage(TestTelegramRequest request, IEnumerable<ToDoItem> expected, string pageText)
    {
        var expectedItems = expected.ToArray();
        var buttons = TaskButtons(request);
        Assert.Equal(expectedItems.Select(item => item.Name), buttons.Select(button => button.Text));
        Assert.Equal(expectedItems.Select(item => item.Id), buttons.Select(button => ToDoItemCallbackDto.FromString(button.Data).ToDoItemId));
        Assert.Contains(pageText, request.Text);
        Assert.InRange(buttons.Count, 1, 5);
    }

    public void Dispose()
    {
        _http.Dispose();
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory))
            System.IO.File.Delete(file);
        Directory.Delete(_directory);
    }
}
