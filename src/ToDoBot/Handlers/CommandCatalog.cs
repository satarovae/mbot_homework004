using Telegram.Bot.Types;

namespace ToDoBot.Handlers;

public static class CommandCatalog
{
    public static readonly BotCommand[] Commands =
    [
        new() { Command = "start", Description = "Начать работу" },
        new() { Command = "help", Description = "Помощь и команды" },
        new() { Command = "show", Description = "Списки и задачи" },
        new() { Command = "addtask", Description = "Добавить задачу" },
        new() { Command = "addlist", Description = "Добавить список" },
        new() { Command = "deletelist", Description = "Удалить список" },
        new() { Command = "report", Description = "Количество задач" },
        new() { Command = "cancel", Description = "Отменить текущий диалог" }
    ];

    public const string Help = """
        /show - выбрать список и открыть задачи
        /addtask - создать задачу с необязательной датой
        /addlist - создать список
        /deletelist - удалить список с подтверждением
        /report - количество активных и выполненных задач
        /cancel - отменить текущий диалог
        /help - показать помощь

        В списке показывается до пяти задач. Стрелки переключают страницы.
        Нажмите название задачи, затем «✅Выполнить» или «❌Удалить».
        Удаление требует отдельного подтверждения; «Отмена» сохраняет задачу.
        Кнопка «☑️Посмотреть выполненные» открывает выполненные задачи.
        Списки и задачи доступны только их владельцу. Бот работает в личном чате.
        """;
}
