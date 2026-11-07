using ToDoBot.Services;

namespace ToDoBot.Scenarios;

public sealed class DeleteTaskScenario(IToDoService tasks) : IScenario
{
    public ScenarioType Type => ScenarioType.DeleteTask;

    public async Task<ScenarioResult> Handle(ScenarioContext context, ScenarioInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input == ScenarioInput.Cancel)
        {
            context.ConfirmationRequested = false;
            return new("Удаление отменено.", true);
        }
        var task = await tasks.Get(context.TargetId, ct);
        if (task is null || task.UserId != context.UserId)
            return new("Задача не найдена или недоступна.", true);
        if (input == ScenarioInput.Start || !context.ConfirmationRequested)
        {
            context.ConfirmationRequested = true;
            return new($"Удалить задачу «{task.Name}»? Это действие нельзя отменить.", false, true);
        }
        context.ConfirmationRequested = false;
        var deleted = await tasks.Delete(context.UserId, task.Id, ct);
        return new(deleted ? "Задача удалена." : "Задача уже удалена.", true);
    }
}
