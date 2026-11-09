namespace BackEnd.Services;

public sealed class HabitApiException(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static HabitApiException Validation(string title) => new(400, "validation_error", title);
    public static HabitApiException Unauthenticated() => new(401, "unauthenticated", "Требуется авторизация");
    public static HabitApiException NotFound() => new(404, "habit_not_found", "Привычка не найдена");
    public static HabitApiException Archived() => new(409, "habit_archived", "Привычка в архиве");
}
