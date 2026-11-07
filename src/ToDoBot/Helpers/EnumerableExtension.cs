namespace ToDoBot.Helpers;

public static class EnumerableExtension
{
    /// <summary>
    /// Returns the zero-based batch without enumerating the source until iteration starts.
    /// Arguments, including the Int32 offset required by Skip, are validated immediately.
    /// </summary>
    public static IEnumerable<T> GetBatchByNumber<T>(
        this IEnumerable<T> source, int batchSize, int batchNumber)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(batchNumber);

        var offset = (long)batchSize * batchNumber;
        if (offset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchNumber), batchNumber, "The batch offset exceeds Int32.MaxValue.");
        }

        return source.Skip((int)offset).Take(batchSize);
    }
}
