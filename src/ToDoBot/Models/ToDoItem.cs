namespace ToDoBot.Models;

public sealed record ToDoItem(
    Guid Id,
    long UserId,
    Guid? ToDoListId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DueDate,
    bool IsCompleted = false,
    DateTimeOffset? CompletedAt = null);
