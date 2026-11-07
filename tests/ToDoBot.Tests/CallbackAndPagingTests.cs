using System.Globalization;
using ToDoBot.Callbacks;
using ToDoBot.Helpers;
using Xunit;

namespace ToDoBot.Tests;

public sealed class CallbackAndPagingTests
{
    private const string IdText = "0123456789abcdef0123456789abcdef";
    private static readonly Guid Id = Guid.ParseExact(IdText, "N");

    [Theory]
    [InlineData("show")]
    [InlineData("show_completed")]
    [InlineData("Confirm_Delete")]
    public void BaseCallbackPreservesAction(string action)
    {
        var dto = new CallbackDto(action);
        Assert.Equal(action, dto.Action);
        Assert.Equal(action, dto.ToString());
        Assert.Equal(action, CallbackDto.FromString(action).Action);
    }

    [Fact]
    public void BaseParserExplicitlyReadsOnlyActionForRouting()
    {
        var input = $"show_completed|{IdText}|7";
        Assert.Equal("show_completed", CallbackDto.ParseAction(input));
        Assert.Equal("show_completed", CallbackDto.FromString(input).Action);
        Assert.Equal("show", CallbackDto.ParseAction("show|payload-is-validated-by-the-concrete-dto"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("show1")]
    [InlineData("show-task")]
    [InlineData("show task")]
    [InlineData(" show")]
    [InlineData("show\n")]
    [InlineData("показать")]
    [InlineData("show|payload")]
    public void ConstructorRejectsInvalidActions(string action)
    {
        Assert.Throws<ArgumentException>(() => new CallbackDto(action));
    }

    [Theory]
    [InlineData("")]
    [InlineData("|payload")]
    [InlineData("show1|payload")]
    [InlineData("show-task|payload")]
    [InlineData("показать|payload")]
    public void ActionParserRejectsInvalidPrefix(string input)
    {
        Assert.Throws<FormatException>(() => CallbackDto.ParseAction(input));
    }

    [Fact]
    public void AllParsersRejectNullImmediately()
    {
        Assert.Throws<ArgumentNullException>(() => new CallbackDto(null!));
        Assert.Throws<ArgumentNullException>(() => CallbackDto.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => CallbackDto.ParseAction(null!));
        Assert.Throws<ArgumentNullException>(() => ToDoListCallbackDto.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => ToDoItemCallbackDto.FromString(null!));
        Assert.Throws<ArgumentNullException>(() => PagedListCallbackDto.FromString(null!));
    }

    [Fact]
    public void ListCallbackRoundTripsGuidInNFormat()
    {
        var dto = new ToDoListCallbackDto("show", Id);
        Assert.Equal($"show|{IdText}", dto.ToString());
        var parsed = ToDoListCallbackDto.FromString(dto.ToString());
        Assert.Equal(dto.Action, parsed.Action);
        Assert.Equal(Id, parsed.ToDoListId);
    }

    [Fact]
    public void UppercaseGuidIsAcceptedAndNormalized()
    {
        var input = $"show|{IdText.ToUpperInvariant()}";
        Assert.Equal($"show|{IdText}", ToDoListCallbackDto.FromString(input).ToString());
    }

    [Fact]
    public void GeneratedCallbacksRoundTripAndRejectDamagedIds()
    {
        var random = new Random(2026);
        var bytes = new byte[16];
        for (var index = 0; index < 100; index++)
        {
            random.NextBytes(bytes);
            var id = new Guid(bytes);
            var page = random.Next(0, int.MaxValue);
            var listDto = new PagedListCallbackDto("show_completed", id, page);
            var parsedList = PagedListCallbackDto.FromString(listDto.ToString());
            Assert.Equal(id, parsedList.ToDoListId);
            Assert.Equal(page, parsedList.Page);

            var itemDto = new ToDoItemCallbackDto("showtask", id);
            Assert.Equal(id, ToDoItemCallbackDto.FromString(itemDto.ToString()).ToDoItemId);

            var damagedId = id.ToString("N").ToCharArray();
            damagedId[random.Next(damagedId.Length)] = 'g';
            var malformed = $"showtask|{new string(damagedId)}";
            Assert.Throws<FormatException>(() => ToDoItemCallbackDto.FromString(malformed));
            Assert.Throws<FormatException>(() => PagedListCallbackDto.FromString($"{malformed}|{page}"));
        }
    }

    [Theory]
    [InlineData("showtask")]
    [InlineData("completetask")]
    [InlineData("deletetask")]
    public void ItemCallbacksKeepTaskIdAndAction(string action)
    {
        var dto = new ToDoItemCallbackDto(action, Id);
        Assert.Equal($"{action}|{IdText}", dto.ToString());
        var parsed = ToDoItemCallbackDto.FromString(dto.ToString());
        Assert.Equal(action, parsed.Action);
        Assert.Equal(Id, parsed.ToDoItemId);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("show|")]
    [InlineData("show|not-a-guid")]
    [InlineData("show|01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("show| 0123456789abcdef0123456789abcdef")]
    [InlineData("show|0123456789abcdef0123456789abcdef ")]
    [InlineData("show|0123456789abcdef0123456789abcdef|0")]
    [InlineData("show|0123456789abcdef0123456789abcdef|")]
    [InlineData("show1|0123456789abcdef0123456789abcdef")]
    public void ConcreteIdParsersRejectMalformedPayloads(string input)
    {
        Assert.Throws<FormatException>(() => ToDoListCallbackDto.FromString(input));
        Assert.Throws<FormatException>(() => ToDoItemCallbackDto.FromString(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void PagedCallbackRoundTripsPageAndVirtualList(int page)
    {
        var dto = new PagedListCallbackDto("show_completed", Guid.Empty, page);
        var expected = $"show_completed|{Guid.Empty:N}|{page.ToString(CultureInfo.InvariantCulture)}";
        Assert.Equal(expected, dto.ToString());
        var parsed = PagedListCallbackDto.FromString(expected);
        Assert.Equal("show_completed", parsed.Action);
        Assert.Equal(Guid.Empty, parsed.ToDoListId);
        Assert.Equal(page, parsed.Page);
    }

    [Fact]
    public void PagedCallbackRejectsNegativeConstructorArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PagedListCallbackDto("show", Id, -1));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("one")]
    [InlineData("")]
    [InlineData("2147483648")]
    [InlineData("0|extra")]
    [InlineData("0\0")]
    public void PagedCallbackRejectsMalformedPages(string page)
    {
        Assert.Throws<FormatException>(() => PagedListCallbackDto.FromString($"show|{IdText}|{page}"));
    }

    [Theory]
    [InlineData("show")]
    [InlineData("show|0123456789abcdef0123456789abcdef")]
    [InlineData("show|invalid|0")]
    [InlineData("show1|0123456789abcdef0123456789abcdef|0")]
    public void PagedCallbackRejectsWrongShapeOrId(string input)
    {
        Assert.Throws<FormatException>(() => PagedListCallbackDto.FromString(input));
    }

    [Fact]
    public void BatchMatchesHomeworkExample()
    {
        var batch = Enumerable.Range(1, 10).GetBatchByNumber(3, 1);
        Assert.Equal(new[] { 4, 5, 6 }, batch);
    }

    [Fact]
    public void BatchHandlesFirstPartialAndMissingPages()
    {
        var source = Enumerable.Range(1, 10);
        Assert.Equal(new[] { 1, 2, 3 }, source.GetBatchByNumber(3, 0));
        Assert.Equal(new[] { 10 }, source.GetBatchByNumber(3, 3));
        Assert.Empty(source.GetBatchByNumber(3, 4));
        Assert.Empty(Array.Empty<int>().GetBatchByNumber(5, 0));
    }

    [Theory]
    [InlineData(0, 0, "batchSize")]
    [InlineData(-1, 0, "batchSize")]
    [InlineData(5, -1, "batchNumber")]
    [InlineData(int.MaxValue, 2, "batchNumber")]
    [InlineData(2, int.MaxValue, "batchNumber")]
    public void BatchValidatesArgumentsBeforeEnumeration(int size, int number, string parameter)
    {
        // Calling the method alone must fail; no enumeration is used to trigger validation.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = Enumerable.Range(1, 3).GetBatchByNumber(size, number);
        });
        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public void BatchRejectsNullBeforeEnumeration()
    {
        Assert.Throws<ArgumentNullException>(() => EnumerableExtension.GetBatchByNumber<int>(null!, 5, 0));
    }

    [Fact]
    public void BatchKeepsSourceDeferred()
    {
        var enumerations = 0;
        IEnumerable<int> Source()
        {
            enumerations++;
            yield return 10;
            yield return 20;
            yield return 30;
        }

        var batch = Source().GetBatchByNumber(1, 1);
        Assert.Equal(0, enumerations);
        Assert.Equal(new[] { 20 }, batch);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void RequestedBatchDoesNotConsumeFollowingElements()
    {
        IEnumerable<int> Source()
        {
            for (var value = 0; value < 6; value++)
            {
                yield return value;
            }

            throw new InvalidOperationException("The requested batch has already ended.");
        }

        Assert.Equal(new[] { 4, 5 }, Source().GetBatchByNumber(2, 2));
    }

    [Fact]
    public void LargestValidOffsetDoesNotWrapAround()
    {
        Assert.Empty(new[] { 1, 2, 3 }.GetBatchByNumber(1, int.MaxValue));
        Assert.Equal(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }.GetBatchByNumber(int.MaxValue, 0));
    }
}
