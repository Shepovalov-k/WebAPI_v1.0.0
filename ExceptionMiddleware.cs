namespace TimescaleApi.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Клиент разорвал соединение — отвечать некому, это не ошибка сервера.
            _logger.LogInformation("Запрос {Path} отменён клиентом", context.Request.Path);
        }
        catch (Exception ex) when (context.Response.HasStarted)
        {
            // Ответ уже начал отправляться — изменить статус нельзя.
            _logger.LogError(ex, "Ошибка после начала отправки ответа");
            throw;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Ошибка валидации данных");
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // Ошибки привязки параметров minimal API (например, нет поля file в форме)
            _logger.LogWarning(ex, "Некорректный запрос");
            await WriteProblemAsync(context, ex.StatusCode, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необработанная ошибка");
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "Внутренняя ошибка сервера.");
        }
    }

    // Пишет стандартный application/problem+json (camelCase, title по статусу)
    private static Task WriteProblemAsync(HttpContext context, int statusCode, string detail) =>
        Results.Problem(detail: detail, statusCode: statusCode).ExecuteAsync(context);
}
