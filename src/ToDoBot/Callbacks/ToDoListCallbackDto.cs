namespace ToDoBot.Callbacks;

public class ToDoListCallbackDto : CallbackDto
{
    public Guid ToDoListId { get; }

    public ToDoListCallbackDto(string action, Guid toDoListId) : base(action)
    {
        // Guid.Empty identifies the virtual list for tasks without a named list.
        ToDoListId = toDoListId;
    }

    public static new ToDoListCallbackDto FromString(string input)
    {
        var segments = ParseSegments(input, 2);
        return new ToDoListCallbackDto(segments[0], ParseGuid(segments[1]));
    }

    public override string ToString() => $"{base.ToString()}|{ToDoListId:N}";
}
