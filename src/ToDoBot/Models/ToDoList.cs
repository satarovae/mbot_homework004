namespace ToDoBot.Models;

public sealed record ToDoList(
    Guid Id,
    long UserId,
    string Name,
    DateTimeOffset CreatedAt);
