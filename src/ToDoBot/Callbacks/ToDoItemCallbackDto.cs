namespace ToDoBot.Callbacks;

public sealed class ToDoItemCallbackDto : CallbackDto
{
    public Guid ToDoItemId { get; }

    public ToDoItemCallbackDto(string action, Guid toDoItemId) : base(action)
    {
        ToDoItemId = toDoItemId;
    }

    public static new ToDoItemCallbackDto FromString(string input)
    {
        var segments = ParseSegments(input, 2);
        return new ToDoItemCallbackDto(segments[0], ParseGuid(segments[1]));
    }

    public override string ToString() => $"{base.ToString()}|{ToDoItemId:N}";
}
