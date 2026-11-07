using ToDoBot.Services;

namespace ToDoBot.Scenarios;

public sealed class DeleteListScenario(IToDoListService lists) : IScenario
{
    public ScenarioType Type => ScenarioType.DeleteList;

    public async Task<ScenarioResult> Handle(ScenarioContext context, ScenarioInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input == ScenarioInput.Cancel)
        {
            context.ConfirmationRequested = false;
            return new("Удаление списка отменено.", true);
        }
        var list = await lists.Get(context.UserId, context.TargetId, ct);
        if (list is null) return new("Список не найден или недоступен.", true);
        if (input == ScenarioInput.Start || !context.ConfirmationRequested)
        {
            context.ConfirmationRequested = true;
            return new($"Удалить список «{list.Name}»? Его задачи останутся в разделе «Без списка».", false, true);
        }
        context.ConfirmationRequested = false;
        return new(await lists.Delete(context.UserId, list.Id, ct) ? "Список удалён. Задачи перемещены в «Без списка»." : "Список уже удалён.", true);
    }
}
