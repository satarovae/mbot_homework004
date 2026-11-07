namespace ToDoBot.Callbacks;

/// <summary>The action shared by the bot's callback data formats.</summary>
public class CallbackDto
{
    public string Action { get; }

    public CallbackDto(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsValidAction(action))
        {
            throw new ArgumentException(
                "Action must contain only ASCII letters and underscores.", nameof(action));
        }

        Action = action;
    }

    /// <summary>
    /// Reads only the action, including when <paramref name="input"/> contains a payload.
    /// Use the concrete DTO's FromString method to validate its complete payload.
    /// </summary>
    public static CallbackDto FromString(string input) => new(ParseAction(input));

    /// <summary>
    /// Validates and reads the segment preceding the first '|', for callback routing.
    /// Subsequent segments are deliberately left to the selected concrete DTO.
    /// </summary>
    public static string ParseAction(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var separator = input.IndexOf('|');
        var action = separator < 0 ? input : input[..separator];
        if (!IsValidAction(action))
        {
            throw new FormatException("Invalid callback action.");
        }

        return action;
    }

    public override string ToString() => Action;

    protected static string[] ParseSegments(string input, int expectedCount)
    {
        ArgumentNullException.ThrowIfNull(input);
        var segments = input.Split('|', StringSplitOptions.None);
        if (segments.Length != expectedCount)
        {
            throw new FormatException($"Expected {expectedCount} callback segments.");
        }

        if (!IsValidAction(segments[0]))
        {
            throw new FormatException("Invalid callback action.");
        }

        return segments;
    }

    protected static Guid ParseGuid(string segment)
    {
        if (segment.Length != 32 || !Guid.TryParseExact(segment, "N", out var id))
        {
            throw new FormatException("A callback ID must be a GUID in N format.");
        }

        return id;
    }

    private static bool IsValidAction(string action)
    {
        if (action.Length == 0)
        {
            return false;
        }

        foreach (var character in action)
        {
            if (character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not '_')
            {
                return false;
            }
        }

        return true;
    }
}
