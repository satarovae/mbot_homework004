using System.Text.Json;
using System.Text.Json.Serialization;
using ToDoBot.Models;

namespace ToDoBot.Services;

public sealed class JsonToDoService : IToDoService, IToDoListService
{
    private const int MaximumNameLength = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonToDoService(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        if (string.IsNullOrEmpty(Path.GetFileName(_filePath)))
        {
            throw new ArgumentException("Нужно указать путь к файлу данных.", nameof(filePath));
        }
    }

    // This contract has no userId; callers must check the returned item's owner.
    public Task<ToDoItem?> Get(Guid toDoItemId, CancellationToken ct) =>
        ReadAsync(store => store.Items.FirstOrDefault(item => item.Id == toDoItemId), ct);

    public Task<IReadOnlyList<ToDoItem>> GetAll(long userId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return ReadAsync<IReadOnlyList<ToDoItem>>(
            store => OrderItems(store.Items.Where(item => item.UserId == userId)), ct);
    }

    public Task<IReadOnlyList<ToDoItem>> GetItems(
        long userId, Guid? listId, bool completed, CancellationToken ct)
    {
        ValidateUserId(userId);
        listId = NormalizeListId(listId);
        return ReadAsync<IReadOnlyList<ToDoItem>>(
            store => OrderItems(store.Items.Where(item =>
                item.UserId == userId && item.ToDoListId == listId && item.IsCompleted == completed)),
            ct);
    }

    public Task<ToDoItem> Add(
        long userId, Guid? listId, string name, DateTimeOffset? dueDate, CancellationToken ct)
    {
        ValidateUserId(userId);
        name = NormalizeName(name);
        listId = NormalizeListId(listId);

        return UpdateAsync(store =>
        {
            if (listId.HasValue && !store.Lists.Any(list =>
                    list.Id == listId.Value && list.UserId == userId))
            {
                throw new InvalidOperationException("Список недоступен текущему пользователю.");
            }

            var item = new ToDoItem(
                Guid.NewGuid(), userId, listId, name, DateTimeOffset.UtcNow, dueDate);
            store.Items.Add(item);
            return (true, item);
        }, ct);
    }

    public Task<bool> Complete(long userId, Guid taskId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return UpdateAsync(store =>
        {
            var index = store.Items.FindIndex(item => item.Id == taskId && item.UserId == userId);
            if (index < 0 || store.Items[index].IsCompleted)
            {
                return (false, false);
            }

            store.Items[index] = store.Items[index] with
            {
                IsCompleted = true,
                CompletedAt = DateTimeOffset.UtcNow
            };
            return (true, true);
        }, ct);
    }

    public Task<bool> Delete(long userId, Guid taskId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return UpdateAsync(store =>
        {
            var removed = store.Items.RemoveAll(item => item.Id == taskId && item.UserId == userId) != 0;
            return (removed, removed);
        }, ct);
    }

    Task<IReadOnlyList<ToDoList>> IToDoListService.GetAll(long userId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return ReadAsync<IReadOnlyList<ToDoList>>(store => store.Lists
            .Where(list => list.UserId == userId)
            .OrderBy(list => list.CreatedAt)
            .ThenBy(list => list.Id)
            .ToArray(), ct);
    }

    public Task<ToDoList?> Get(long userId, Guid listId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return ReadAsync(store => store.Lists.FirstOrDefault(list =>
            list.Id == listId && list.UserId == userId), ct);
    }

    public Task<ToDoList> Add(long userId, string name, CancellationToken ct)
    {
        ValidateUserId(userId);
        name = NormalizeName(name);
        return UpdateAsync(store =>
        {
            var list = new ToDoList(Guid.NewGuid(), userId, name, DateTimeOffset.UtcNow);
            store.Lists.Add(list);
            return (true, list);
        }, ct);
    }

    Task<bool> IToDoListService.Delete(long userId, Guid listId, CancellationToken ct)
    {
        ValidateUserId(userId);
        return UpdateAsync(store =>
        {
            var removed = store.Lists.RemoveAll(list => list.Id == listId && list.UserId == userId) != 0;
            if (!removed)
            {
                return (false, false);
            }

            for (var index = 0; index < store.Items.Count; index++)
            {
                var item = store.Items[index];
                if (item.UserId == userId && item.ToDoListId == listId)
                {
                    store.Items[index] = item with { ToDoListId = null };
                }
            }

            return (true, true);
        }, ct);
    }

    private async Task<TResult> ReadAsync<TResult>(
        Func<DataStore, TResult> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await LoadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return read(store);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TResult> UpdateAsync<TResult>(
        Func<DataStore, (bool Changed, TResult Result)> update, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read and mutate a fresh candidate so a failed write never changes cached state.
            var store = await LoadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = update(store);
            ct.ThrowIfCancellationRequested();
            if (result.Changed)
            {
                await SaveAsync(store, ct).ConfigureAwait(false);
            }

            return result.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DataStore> LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var store = await JsonSerializer.DeserializeAsync<DataStore>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            if (store is null)
            {
                throw InvalidStore();
            }

            ValidateStore(store);
            return store;
        }
        catch (FileNotFoundException)
        {
            return new DataStore();
        }
        catch (DirectoryNotFoundException)
        {
            return new DataStore();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Файл данных содержит повреждённый JSON. Исходный файл не изменён.", exception);
        }
    }

    private async Task SaveAsync(DataStore store, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // The final same-directory rename is the commit point. Do not report cancellation
            // after it: at that point the operation has already been persisted.
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the original error if cleanup cannot remove an abandoned temp file.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not disguise a failed commit or a successfully committed change.
            }
        }
    }

    private static ToDoItem[] OrderItems(IEnumerable<ToDoItem> items) => items
        .OrderBy(item => item.CreatedAt)
        .ThenBy(item => item.Id)
        .ToArray();

    private static Guid? NormalizeListId(Guid? listId) =>
        listId == Guid.Empty ? null : listId;

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Имя не должно быть пустым.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"Имя не должно превышать {MaximumNameLength} символов.", nameof(name));
        }

        return normalized;
    }

    private static void ValidateUserId(long userId)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId),
                "Идентификатор пользователя должен быть положительным.");
        }
    }

    private static void ValidateStore(DataStore store)
    {
        if (store.Version != 1 || store.Lists is null || store.Items is null)
        {
            throw InvalidStore();
        }

        var lists = new Dictionary<Guid, long>();
        foreach (var list in store.Lists)
        {
            if (list is null || list.Id == Guid.Empty || list.UserId <= 0 ||
                list.CreatedAt == default || !IsStoredNameValid(list.Name) ||
                !lists.TryAdd(list.Id, list.UserId))
            {
                throw InvalidStore();
            }
        }

        var itemIds = new HashSet<Guid>();
        foreach (var item in store.Items)
        {
            if (item is null || item.Id == Guid.Empty || item.UserId <= 0 ||
                item.CreatedAt == default || !IsStoredNameValid(item.Name) ||
                !itemIds.Add(item.Id) || item.IsCompleted != item.CompletedAt.HasValue)
            {
                throw InvalidStore();
            }

            if (item.ToDoListId.HasValue &&
                (!lists.TryGetValue(item.ToDoListId.Value, out var ownerId) || ownerId != item.UserId))
            {
                throw InvalidStore();
            }
        }
    }

    private static bool IsStoredNameValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaximumNameLength && name == name.Trim();

    private static InvalidDataException InvalidStore() => new(
        "Структура файла данных повреждена или имеет неподдерживаемую версию. Исходный файл не изменён.");

    private sealed class DataStore
    {
        public DataStore()
        {
        }

        public int Version { get; init; } = 1;

        [JsonRequired]
        public List<ToDoList> Lists { get; init; } = [];

        [JsonRequired]
        public List<ToDoItem> Items { get; init; } = [];
    }
}
