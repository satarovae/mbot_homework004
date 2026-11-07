using ToDoBot.Handlers;
using ToDoBot.Scenarios;
using ToDoBot.Services;
using Xunit;

namespace ToDoBot.Tests;

public sealed class ScenarioTests : IDisposable
{
    private const long OwnerId = 41;
    private const long OtherUserId = 99;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _directory;
    private readonly string _dataFile;
    private readonly IToDoService _tasks;
    private readonly IToDoListService _lists;

    public ScenarioTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ToDoBot.ScenarioTests", Guid.NewGuid().ToString("N"));
        _dataFile = Path.Combine(_directory, "tasks.json");
        var store = new JsonToDoService(_dataFile);
        _tasks = store;
        _lists = store;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRequestsConfirmationWithoutDeleting(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);

        var result = await scenario.Handle(context, ScenarioInput.Start, Ct);

        Assert.False(result.Finished);
        Assert.True(result.AwaitingConfirmation);
        Assert.True(context.ConfirmationRequested);
        Assert.True(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmWithoutStartOnlyRequestsConfirmation(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);

        var result = await scenario.Handle(context, ScenarioInput.Confirm, Ct);

        Assert.False(result.Finished);
        Assert.True(result.AwaitingConfirmation);
        Assert.True(context.ConfirmationRequested);
        Assert.True(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmAfterStartDeletesTheSelectedTarget(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);
        await scenario.Handle(context, ScenarioInput.Start, Ct);

        var result = await scenario.Handle(context, ScenarioInput.Confirm, Ct);

        Assert.True(result.Finished);
        Assert.False(result.AwaitingConfirmation);
        Assert.False(context.ConfirmationRequested);
        Assert.False(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAfterStartPreservesTheTarget(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);
        await scenario.Handle(context, ScenarioInput.Start, Ct);

        var result = await scenario.Handle(context, ScenarioInput.Cancel, Ct);

        Assert.True(result.Finished);
        Assert.False(result.AwaitingConfirmation);
        Assert.False(context.ConfirmationRequested);
        Assert.True(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmAfterCancelRequiresFreshConfirmation(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);
        await scenario.Handle(context, ScenarioInput.Start, Ct);
        await scenario.Handle(context, ScenarioInput.Cancel, Ct);

        var result = await scenario.Handle(context, ScenarioInput.Confirm, Ct);

        Assert.False(result.Finished);
        Assert.True(result.AwaitingConfirmation);
        Assert.True(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false, ScenarioInput.Start)]
    [InlineData(false, ScenarioInput.Confirm)]
    [InlineData(true, ScenarioInput.Start)]
    [InlineData(true, ScenarioInput.Confirm)]
    public async Task ForeignUserCannotStartOrConfirmDeletion(bool isList, ScenarioInput input)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OtherUserId, id)
        {
            // Ownership must still be checked even if a confirmation flag was supplied.
            ConfirmationRequested = true
        };

        var result = await scenario.Handle(context, input, Ct);

        Assert.True(result.Finished);
        Assert.False(result.AwaitingConfirmation);
        Assert.True(await TargetExists(isList, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedConfirmationDoesNotDeleteAgainOrTouchAnotherTarget(bool isList)
    {
        var (id, scenario) = await CreateTarget(isList);
        var context = new ScenarioContext(OwnerId, id);
        await scenario.Handle(context, ScenarioInput.Start, Ct);
        await scenario.Handle(context, ScenarioInput.Confirm, Ct);
        var (remainingId, _) = await CreateTarget(isList);
        var beforeRepeat = await File.ReadAllBytesAsync(_dataFile, Ct);

        var result = await scenario.Handle(context, ScenarioInput.Confirm, Ct);

        Assert.True(result.Finished);
        Assert.False(result.AwaitingConfirmation);
        Assert.False(await TargetExists(isList, id));
        Assert.True(await TargetExists(isList, remainingId));
        Assert.Equal(beforeRepeat, await File.ReadAllBytesAsync(_dataFile, Ct));
    }

    [Fact]
    public async Task DeletingListMovesItsTasksAndKeepsTheirData()
    {
        var list = await _lists.Add(OwnerId, "Удаляемый список", Ct);
        var otherList = await _lists.Add(OwnerId, "Другой список", Ct);
        var foreignList = await _lists.Add(OtherUserId, "Список другого пользователя", Ct);
        var dueDate = new DateTimeOffset(2030, 5, 7, 12, 30, 0, TimeSpan.Zero);
        var active = await _tasks.Add(OwnerId, list.Id, "Активная задача", dueDate, Ct);
        var completed = await _tasks.Add(OwnerId, list.Id, "Выполненная задача", null, Ct);
        await _tasks.Complete(OwnerId, completed.Id, Ct);
        var completedBefore = await _tasks.Get(completed.Id, Ct);
        var alreadyUnlisted = await _tasks.Add(OwnerId, null, "Изначально без списка", null, Ct);
        var unrelated = await _tasks.Add(OwnerId, otherList.Id, "В другом списке", null, Ct);
        var foreign = await _tasks.Add(OtherUserId, foreignList.Id, "Чужая задача", null, Ct);
        var scenario = new DeleteListScenario(_lists);
        var context = new ScenarioContext(OwnerId, list.Id);
        await scenario.Handle(context, ScenarioInput.Start, Ct);

        var result = await scenario.Handle(context, ScenarioInput.Confirm, Ct);

        Assert.True(result.Finished);
        Assert.Null(await _lists.Get(OwnerId, list.Id, Ct));
        // Reopen the real file through a new service to verify the committed result.
        var reopened = new JsonToDoService(_dataFile);
        var activeAfter = await reopened.Get(active.Id, Ct);
        var completedAfter = await reopened.Get(completed.Id, Ct);
        Assert.NotNull(activeAfter);
        Assert.NotNull(completedAfter);
        Assert.Equal(active with { ToDoListId = null }, activeAfter);
        Assert.Equal(completedBefore! with { ToDoListId = null }, completedAfter);
        Assert.Equal(alreadyUnlisted, await reopened.Get(alreadyUnlisted.Id, Ct));
        Assert.Equal(unrelated, await reopened.Get(unrelated.Id, Ct));
        Assert.Equal(foreign, await reopened.Get(foreign.Id, Ct));
        Assert.Contains(await reopened.GetItems(OwnerId, null, false, Ct), item => item.Id == active.Id);
        Assert.Contains(await reopened.GetItems(OwnerId, null, true, Ct), item => item.Id == completed.Id);
    }

    [Fact]
    public void ScenarioTypesIdentifyBothDeletionFlows()
    {
        Assert.Equal(ScenarioType.DeleteTask, new DeleteTaskScenario(_tasks).Type);
        Assert.Equal(ScenarioType.DeleteList, new DeleteListScenario(_lists).Type);
    }

    [Fact]
    public void CommandCatalogContainsTheEightSupportedCommands()
    {
        string[] expected = ["start", "help", "show", "addtask", "addlist", "deletelist", "report", "cancel"];
        var actual = CommandCatalog.Commands.Select(command => command.Command).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("completetask", actual);
        Assert.DoesNotContain("removetask", actual);
        Assert.All(CommandCatalog.Commands, command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));
    }

    [Fact]
    public void HelpExplainsTaskButtonsConfirmationAndPaging()
    {
        Assert.Contains("Выполнить", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Удалить", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("подтвержден", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Отмена", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Посмотреть выполненные", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Стрелки", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("страниц", CommandCatalog.Help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/completetask", CommandCatalog.Help);
        Assert.DoesNotContain("/removetask", CommandCatalog.Help);
    }

    private async Task<(Guid Id, IScenario Scenario)> CreateTarget(bool isList)
    {
        if (isList)
        {
            var list = await _lists.Add(OwnerId, "Список для удаления", Ct);
            return (list.Id, new DeleteListScenario(_lists));
        }

        var task = await _tasks.Add(OwnerId, null, "Задача для удаления", null, Ct);
        return (task.Id, new DeleteTaskScenario(_tasks));
    }

    private async Task<bool> TargetExists(bool isList, Guid id)
    {
        return isList
            ? await _lists.Get(OwnerId, id, Ct) is not null
            : await _tasks.Get(id, Ct) is not null;
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ToDoBot.ScenarioTests"))
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(path), "N", out _))
        {
            throw new InvalidOperationException("Refusing to clean an unexpected test directory.");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
