using ToDoBot.Models;

namespace ToDoBot.Services;

public interface IToDoService
{
    Task<ToDoItem?> Get(Guid toDoItemId, CancellationToken ct);

    Task<IReadOnlyList<ToDoItem>> GetAll(long userId, CancellationToken ct);

    Task<IReadOnlyList<ToDoItem>> GetItems(
        long userId, Guid? listId, bool completed, CancellationToken ct);

    Task<ToDoItem> Add(
        long userId, Guid? listId, string name, DateTimeOffset? dueDate, CancellationToken ct);

    Task<bool> Complete(long userId, Guid taskId, CancellationToken ct);

    Task<bool> Delete(long userId, Guid taskId, CancellationToken ct);
}
