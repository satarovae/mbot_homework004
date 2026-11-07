namespace ToDoBot.Scenarios;

public enum ScenarioType { DeleteTask, DeleteList }
public enum ScenarioInput { Start, Confirm, Cancel }

public sealed record ScenarioContext(long UserId, Guid TargetId)
{
    public bool ConfirmationRequested { get; set; }
}

public sealed record ScenarioResult(string Text, bool Finished, bool AwaitingConfirmation = false);

public interface IScenario
{
    ScenarioType Type { get; }
    Task<ScenarioResult> Handle(ScenarioContext context, ScenarioInput input, CancellationToken ct);
}
