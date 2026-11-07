using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToDoBot.Services;
using Xunit;

namespace ToDoBot.Tests;

public sealed class ServiceTests : IDisposable
{
    private const long Owner = 1001;
    private const long OtherUser = 2002;
    private readonly string _directory;
    private readonly string _filePath;

    public ServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ToDoBot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "todos.json");
    }

    [Fact]
    public async Task NewStorageReturnsEmptyCollections()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;

        Assert.Empty(await tasks.GetAll(Owner, CancellationToken.None));
        Assert.Empty(await lists.GetAll(Owner, CancellationToken.None));
        Assert.Empty(await tasks.GetItems(Owner, null, false, CancellationToken.None));
        Assert.Null(await tasks.Get(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await lists.Get(Owner, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task SavedListsAndTasksSurviveReopeningIncludingDatesAndCompletion()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var dueDate = new DateTimeOffset(2030, 7, 15, 12, 30, 0, TimeSpan.FromHours(7));
        var list = await lists.Add(Owner, "Дом", CancellationToken.None);
        var task = await tasks.Add(Owner, list.Id, "Купить продукты", dueDate, CancellationToken.None);
        var unlisted = await tasks.Add(Owner, null, "Позвонить", null, CancellationToken.None);
        Assert.True(await tasks.Complete(Owner, task.Id, CancellationToken.None));
        var completed = await tasks.Get(task.Id, CancellationToken.None);

        var reopenedStore = CreateStore();
        IToDoService reopenedTasks = reopenedStore;
        IToDoListService reopenedLists = reopenedStore;

        Assert.Equal(list, Assert.Single(await reopenedLists.GetAll(Owner, CancellationToken.None)));
        Assert.Equal(completed, await reopenedTasks.Get(task.Id, CancellationToken.None));
        Assert.Equal(unlisted, await reopenedTasks.Get(unlisted.Id, CancellationToken.None));
        var saved = await reopenedTasks.Get(task.Id, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.IsCompleted);
        Assert.NotNull(saved.CompletedAt);
        Assert.Equal(dueDate, saved.DueDate);
        Assert.Equal(dueDate.Offset, saved.DueDate!.Value.Offset);
        Assert.Equal(task.CreatedAt, saved.CreatedAt);
    }

    [Fact]
    public async Task GetAllAndScopedListGetDoNotExposeOtherUsersData()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var ownList = await lists.Add(Owner, "Мои дела", CancellationToken.None);
        var foreignList = await lists.Add(OtherUser, "Другие дела", CancellationToken.None);
        var ownTask = await tasks.Add(Owner, ownList.Id, "Моя задача", null, CancellationToken.None);
        var foreignTask = await tasks.Add(OtherUser, foreignList.Id, "Чужая задача", null, CancellationToken.None);

        Assert.Equal(ownList, Assert.Single(await lists.GetAll(Owner, CancellationToken.None)));
        Assert.Equal(foreignList, Assert.Single(await lists.GetAll(OtherUser, CancellationToken.None)));
        Assert.Equal(ownTask, Assert.Single(await tasks.GetAll(Owner, CancellationToken.None)));
        Assert.Equal(foreignTask, Assert.Single(await tasks.GetAll(OtherUser, CancellationToken.None)));
        Assert.Null(await lists.Get(Owner, foreignList.Id, CancellationToken.None));
        Assert.Null(await lists.Get(OtherUser, ownList.Id, CancellationToken.None));
        Assert.Empty(await tasks.GetItems(Owner, foreignList.Id, false, CancellationToken.None));
    }

    [Fact]
    public async Task GetItemsSeparatesActiveCompletedListedAndUnlistedTasks()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var firstList = await lists.Add(Owner, "Первый", CancellationToken.None);
        var secondList = await lists.Add(Owner, "Второй", CancellationToken.None);
        var active = await tasks.Add(Owner, firstList.Id, "Активная", null, CancellationToken.None);
        var completed = await tasks.Add(Owner, firstList.Id, "Готовая", null, CancellationToken.None);
        var anotherListTask = await tasks.Add(Owner, secondList.Id, "Другой список", null, CancellationToken.None);
        var unlisted = await tasks.Add(Owner, null, "Без списка", null, CancellationToken.None);
        await tasks.Add(OtherUser, null, "Другой пользователь", null, CancellationToken.None);
        Assert.True(await tasks.Complete(Owner, completed.Id, CancellationToken.None));

        Assert.Equal(active.Id, Assert.Single(await tasks.GetItems(Owner, firstList.Id, false, CancellationToken.None)).Id);
        Assert.Equal(completed.Id, Assert.Single(await tasks.GetItems(Owner, firstList.Id, true, CancellationToken.None)).Id);
        Assert.Equal(anotherListTask.Id, Assert.Single(await tasks.GetItems(Owner, secondList.Id, false, CancellationToken.None)).Id);
        Assert.Equal(unlisted.Id, Assert.Single(await tasks.GetItems(Owner, null, false, CancellationToken.None)).Id);
        Assert.Empty(await tasks.GetItems(Owner, null, true, CancellationToken.None));
        Assert.Empty(await tasks.GetItems(Owner, secondList.Id, true, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyGuidRepresentsTheVirtualUnlistedGroup()
    {
        IToDoService tasks = CreateStore();
        var task = await tasks.Add(Owner, Guid.Empty, "Без списка", null, CancellationToken.None);

        Assert.Null(task.ToDoListId);
        Assert.Equal(task.Id, Assert.Single(await tasks.GetItems(Owner, null, false, CancellationToken.None)).Id);
        Assert.Equal(task.Id, Assert.Single(await tasks.GetItems(Owner, Guid.Empty, false, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task AddingToForeignOrMissingListIsRejectedWithoutChangingStorage()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var foreignList = await lists.Add(OtherUser, "Чужой список", CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(_filePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tasks.Add(Owner, foreignList.Id, "Нельзя добавить", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tasks.Add(Owner, Guid.NewGuid(), "Несуществующий список", null, CancellationToken.None));

        Assert.Empty(await tasks.GetAll(Owner, CancellationToken.None));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_filePath));
        Assert.Equal(foreignList, await lists.Get(OtherUser, foreignList.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CompletingRequiresTheOwnerAndDoesNotChangeAnAlreadyCompletedTask()
    {
        IToDoService tasks = CreateStore();
        var task = await tasks.Add(Owner, null, "Завершить", null, CancellationToken.None);

        Assert.False(await tasks.Complete(OtherUser, task.Id, CancellationToken.None));
        Assert.Equal(task, await tasks.Get(task.Id, CancellationToken.None));
        Assert.False(await tasks.Complete(Owner, Guid.NewGuid(), CancellationToken.None));
        Assert.True(await tasks.Complete(Owner, task.Id, CancellationToken.None));
        var completed = await tasks.Get(task.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.False(await tasks.Complete(Owner, task.Id, CancellationToken.None));
        Assert.Equal(completed, await tasks.Get(task.Id, CancellationToken.None));
        IToDoService reopened = CreateStore();
        Assert.Equal(completed, await reopened.Get(task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingRequiresTheOwnerAndRepeatedDeletionReturnsFalse()
    {
        IToDoService tasks = CreateStore();
        var task = await tasks.Add(Owner, null, "Удалить", null, CancellationToken.None);

        Assert.False(await tasks.Delete(OtherUser, task.Id, CancellationToken.None));
        Assert.Equal(task, await tasks.Get(task.Id, CancellationToken.None));
        Assert.False(await tasks.Delete(Owner, Guid.NewGuid(), CancellationToken.None));
        Assert.True(await tasks.Delete(Owner, task.Id, CancellationToken.None));
        Assert.Null(await tasks.Get(task.Id, CancellationToken.None));
        Assert.False(await tasks.Delete(Owner, task.Id, CancellationToken.None));

        IToDoService reopened = CreateStore();
        Assert.Null(await reopened.Get(task.Id, CancellationToken.None));
        Assert.Empty(await reopened.GetAll(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingListMovesBothActiveAndCompletedTasksToUnlistedWithoutLosingData()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var deletedList = await lists.Add(Owner, "Удаляемый", CancellationToken.None);
        var remainingList = await lists.Add(Owner, "Оставшийся", CancellationToken.None);
        var dueDate = new DateTimeOffset(2030, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var active = await tasks.Add(Owner, deletedList.Id, "Активная", dueDate, CancellationToken.None);
        var completed = await tasks.Add(Owner, deletedList.Id, "Выполненная", null, CancellationToken.None);
        var untouched = await tasks.Add(Owner, remainingList.Id, "Не менять", null, CancellationToken.None);
        var initiallyUnlisted = await tasks.Add(Owner, null, "Изначально без списка", null, CancellationToken.None);
        Assert.True(await tasks.Complete(Owner, completed.Id, CancellationToken.None));
        var completedBeforeDeletion = await tasks.Get(completed.Id, CancellationToken.None);
        Assert.NotNull(completedBeforeDeletion);

        Assert.True(await lists.Delete(Owner, deletedList.Id, CancellationToken.None));
        Assert.False(await lists.Delete(Owner, deletedList.Id, CancellationToken.None));
        Assert.Null(await lists.Get(Owner, deletedList.Id, CancellationToken.None));
        Assert.Equal(remainingList, Assert.Single(await lists.GetAll(Owner, CancellationToken.None)));
        Assert.Equal(active with { ToDoListId = null }, await tasks.Get(active.Id, CancellationToken.None));
        Assert.Equal(completedBeforeDeletion! with { ToDoListId = null }, await tasks.Get(completed.Id, CancellationToken.None));
        Assert.Equal(untouched, await tasks.Get(untouched.Id, CancellationToken.None));
        Assert.Equal(initiallyUnlisted, await tasks.Get(initiallyUnlisted.Id, CancellationToken.None));
        Assert.Equal(2, (await tasks.GetItems(Owner, null, false, CancellationToken.None)).Count);
        Assert.Equal(completed.Id, Assert.Single(await tasks.GetItems(Owner, null, true, CancellationToken.None)).Id);

        var reopenedStore = CreateStore();
        IToDoService reopenedTasks = reopenedStore;
        IToDoListService reopenedLists = reopenedStore;
        Assert.Null(await reopenedLists.Get(Owner, deletedList.Id, CancellationToken.None));
        Assert.Equal(active with { ToDoListId = null }, await reopenedTasks.Get(active.Id, CancellationToken.None));
        Assert.Equal(completedBeforeDeletion! with { ToDoListId = null }, await reopenedTasks.Get(completed.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingAnotherUsersListDoesNotMoveOrDeleteItsTasks()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var foreignList = await lists.Add(OtherUser, "Чужой список", CancellationToken.None);
        var foreignTask = await tasks.Add(OtherUser, foreignList.Id, "Чужая задача", null, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(_filePath);

        Assert.False(await lists.Delete(Owner, foreignList.Id, CancellationToken.None));
        Assert.False(await lists.Delete(Owner, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(foreignList, await lists.Get(OtherUser, foreignList.Id, CancellationToken.None));
        Assert.Equal(foreignTask, await tasks.Get(foreignTask.Id, CancellationToken.None));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_filePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task BlankNamesAreRejectedForBothListsAndTasks(string? name)
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => lists.Add(Owner, name!, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tasks.Add(Owner, null, name!, null, CancellationToken.None));
        Assert.Empty(await lists.GetAll(Owner, CancellationToken.None));
        Assert.Empty(await tasks.GetAll(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task NamesAreTrimmedAndThe120CharacterBoundaryIsEnforced()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var validName = new string('я', 120);

        var list = await lists.Add(Owner, $"  {validName}  ", CancellationToken.None);
        var task = await tasks.Add(Owner, list.Id, $"\t{validName}\r\n", null, CancellationToken.None);
        Assert.Equal(validName, list.Name);
        Assert.Equal(validName, task.Name);

        var tooLong = new string('я', 121);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => lists.Add(Owner, tooLong, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tasks.Add(Owner, null, tooLong, null, CancellationToken.None));
        Assert.Single(await lists.GetAll(Owner, CancellationToken.None));
        Assert.Single(await tasks.GetAll(Owner, CancellationToken.None));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task NonPositiveUserIdsAreRejected(long userId)
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lists.Add(userId, "Список", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tasks.Add(userId, null, "Задача", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lists.GetAll(userId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tasks.GetAll(userId, CancellationToken.None));
    }

    [Fact]
    public async Task PreCanceledOperationsCannotChangePersistedDataAndDoNotPoisonTheService()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var list = await lists.Add(Owner, "Сохранить список", CancellationToken.None);
        var task = await tasks.Add(Owner, list.Id, "Сохранить задачу", null, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(_filePath);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var ct = source.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lists.Add(Owner, "Отменённый список", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks.Add(Owner, list.Id, "Отменённая задача", null, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks.Complete(Owner, task.Id, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks.Delete(Owner, task.Id, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lists.Delete(Owner, list.Id, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tasks.GetAll(Owner, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lists.GetAll(Owner, ct));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_filePath));
        Assert.Equal(task, await tasks.Get(task.Id, CancellationToken.None));
        Assert.Equal(list, await lists.Get(Owner, list.Id, CancellationToken.None));
        Assert.True(await tasks.Complete(Owner, task.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"lists\":null,\"items\":[]}")]
    [InlineData("{\"version\":1,\"lists\":[],\"items\":null}")]
    [InlineData("{\"version\":2,\"lists\":[],\"items\":[]}")]
    public async Task CorruptStorageIsRejectedWithoutOverwritingTheOriginalBytes(string invalidJson)
    {
        var originalBytes = Encoding.UTF8.GetBytes(invalidJson);
        await File.WriteAllBytesAsync(_filePath, originalBytes);

        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        await Assert.ThrowsAsync<InvalidDataException>(() => tasks.GetAll(Owner, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => tasks.Add(Owner, null, "Не перезаписывать", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => lists.Add(Owner, "Не перезаписывать", CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_filePath));
    }

    [Fact]
    public async Task CorruptionAfterASuccessfulSaveCannotBeSilentlyReplacedByCachedData()
    {
        IToDoService tasks = CreateStore();
        await tasks.Add(Owner, null, "Существующая задача", null, CancellationToken.None);
        var corruptedBytes = Encoding.UTF8.GetBytes("{\"version\":1,\"lists\":[],\"items\":");
        await File.WriteAllBytesAsync(_filePath, corruptedBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            tasks.Add(Owner, null, "Новая задача", null, CancellationToken.None));

        Assert.Equal(corruptedBytes, await File.ReadAllBytesAsync(_filePath));
    }

    [Fact]
    public async Task ParallelAddsProduceUniqueIdsAndAllTasksSurviveReopening()
    {
        var store = CreateStore();
        IToDoService tasks = store;
        IToDoListService lists = store;
        var list = await lists.Add(Owner, "Параллельные задачи", CancellationToken.None);

        var added = await Task.WhenAll(Enumerable.Range(0, 50).Select(index =>
            tasks.Add(Owner, list.Id, $"Задача {index}", null, CancellationToken.None)));

        Assert.Equal(50, added.Select(item => item.Id).Distinct().Count());
        Assert.All(added, item =>
        {
            Assert.NotEqual(Guid.Empty, item.Id);
            Assert.Equal(Owner, item.UserId);
            Assert.Equal(list.Id, item.ToDoListId);
        });
        var expectedIds = added.Select(item => item.Id).OrderBy(id => id).ToArray();
        var all = await tasks.GetAll(Owner, CancellationToken.None);
        Assert.Equal(expectedIds, all.Select(item => item.Id).OrderBy(id => id).ToArray());

        IToDoService reopened = CreateStore();
        var saved = await reopened.GetItems(Owner, list.Id, false, CancellationToken.None);
        Assert.Equal(expectedIds, saved.Select(item => item.Id).OrderBy(id => id).ToArray());
        Assert.Equal(Enumerable.Range(0, 50).Select(index => $"Задача {index}").OrderBy(name => name),
            saved.Select(item => item.Name).OrderBy(name => name));
        Assert.Equal(saved.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Select(item => item.Id),
            saved.Select(item => item.Id));
    }

    private JsonToDoService CreateStore() => new(_filePath);

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            File.Delete(file);
        }

        Directory.Delete(_directory);
    }
}
