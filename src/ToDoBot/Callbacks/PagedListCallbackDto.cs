using System.Globalization;

namespace ToDoBot.Callbacks;

public sealed class PagedListCallbackDto : ToDoListCallbackDto
{
    public int Page { get; }

    public PagedListCallbackDto(string action, Guid toDoListId, int page)
        : base(action, toDoListId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        Page = page;
    }

    public static new PagedListCallbackDto FromString(string input)
    {
        var segments = ParseSegments(input, 3);
        var listId = ParseGuid(segments[1]);
        if (segments[2].Length == 0
            || segments[2].Any(character => character is < '0' or > '9')
            || !int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var page))
        {
            throw new FormatException("A callback page must be a non-negative Int32.");
        }

        return new PagedListCallbackDto(segments[0], listId, page);
    }

    public override string ToString() => $"{base.ToString()}|{Page.ToString(CultureInfo.InvariantCulture)}";
}
