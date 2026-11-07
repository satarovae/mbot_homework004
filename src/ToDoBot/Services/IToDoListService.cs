using ToDoBot.Models;

namespace ToDoBot.Services;

public interface IToDoListService
{
    Task<IReadOnlyList<ToDoList>> GetAll(long userId, CancellationToken ct);

    Task<ToDoList?> Get(long userId, Guid listId, CancellationToken ct);

    Task<ToDoList> Add(long userId, string name, CancellationToken ct);

    Task<bool> Delete(long userId, Guid listId, CancellationToken ct);
}
