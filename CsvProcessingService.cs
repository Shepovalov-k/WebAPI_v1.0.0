using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TimescaleApi.Data;
using TimescaleApi.DTOs;
using TimescaleApi.Models;

namespace TimescaleApi.Services;

public class CsvProcessingService : ICsvProcessingService
{
    private const int MaxDataRows = 10_000;

    private readonly AppDbContext _dbContext;
    private static readonly DateTimeOffset MinAllowedDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public CsvProcessingService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CsvUploadResult> ProcessCsvAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        // Чтение и валидация до начала транзакции
        var parsedValues = await ParseAndValidateFileAsync(file, cancellationToken);
        var fileName = file.FileName;

        // Если что-то упадёт до CommitAsync, транзакция откатится при Dispose.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Атомарно создаём запись о файле (если её нет) и блокируем строку,
        // чтобы параллельные загрузки одного и того же файла выполнялись по очереди.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""Files"" (""FileName"") VALUES ({fileName}) ON CONFLICT (""FileName"") DO NOTHING",
            cancellationToken);

        var fileIds = await _dbContext.Database
            .SqlQuery<int>($@"SELECT ""Id"" AS ""Value"" FROM ""Files"" WHERE ""FileName"" = {fileName} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var fileId = fileIds.Single();

        // Удаляем старые данные одним запросом, без загрузки сущностей в память
        await _dbContext.Values.Where(v => v.FileId == fileId).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.Results.Where(r => r.FileId == fileId).ExecuteDeleteAsync(cancellationToken);

        foreach (var value in parsedValues)
        {
            value.FileId = fileId;
        }
        _dbContext.Values.AddRange(parsedValues);

        var result = ComputeResult(parsedValues);
        result.FileId = fileId;
        _dbContext.Results.Add(result);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CsvUploadResult
        {
            FileName = fileName,
            RowsProcessed = parsedValues.Count,
            Result = new ResultDto
            {
                FileName = fileName,
                DeltaTimeSeconds = result.DeltaTimeSeconds,
                MinDate = result.MinDate,
                AvgExecutionTime = result.AvgExecutionTime,
                AvgValue = result.AvgValue,
                MedianValue = result.MedianValue,
                MaxValue = result.MaxValue,
                MinValue = result.MinValue
            }
        };
    }

    private static async Task<List<ValueRecord>> ParseAndValidateFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            throw new InvalidDataException("Файл пустой.");

        // (номер строки в файле, текст). Пустые строки пропускаются.
        var lines = new List<(int Number, string Text)>();
        var headerChecked = false;
        var hasHeader = false;
        var lineNumber = 0;

        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!headerChecked)
                {
                    headerChecked = true;
                    if (IsHeader(line))
                    {
                        hasHeader = true;
                        continue;
                    }
                }

                lines.Add((lineNumber, line));
                if (lines.Count > MaxDataRows)
                    throw new InvalidDataException("Количество строк не может быть больше 10 000.");
            }
        }

        if (lines.Count < 1)
            throw new InvalidDataException(hasHeader
                ? "Количество строк не может быть меньше 1."
                : "Файл не содержит строк.");

        var values = new List<ValueRecord>(lines.Count);
        var errors = new List<string>();

        foreach (var (number, text) in lines)
        {
            var parts = text.Split(';');
            if (parts.Length != 3)
            {
                errors.Add($"Строка {number}: ожидается 3 колонки, получено {parts.Length}.");
                continue;
            }

            var dateStr = parts[0].Trim();
            var execTimeStr = parts[1].Trim();
            var valueStr = parts[2].Trim();

            if (string.IsNullOrEmpty(dateStr) || string.IsNullOrEmpty(execTimeStr) || string.IsNullOrEmpty(valueStr))
            {
                errors.Add($"Строка {number}: одно из значений отсутствует.");
                continue;
            }

            // Дата без смещения трактуется как UTC, а не как локальное время сервера
            if (!DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                errors.Add($"Строка {number}: неверный формат даты.");
                continue;
            }

            if (date < MinAllowedDate)
            {
                errors.Add($"Строка {number}: дата не может быть раньше 01.01.2000.");
                continue;
            }
            if (date > DateTimeOffset.UtcNow)
            {
                errors.Add($"Строка {number}: дата не может быть позже текущей.");
                continue;
            }

            // double.IsFinite отсекает NaN и ±Infinity, которые double.TryParse принимает
            if (!double.TryParse(execTimeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var execTime)
                || !double.IsFinite(execTime))
            {
                errors.Add($"Строка {number}: неверный формат времени выполнения.");
                continue;
            }
            if (execTime < 0)
            {
                errors.Add($"Строка {number}: время выполнения не может быть меньше 0.");
                continue;
            }

            if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                errors.Add($"Строка {number}: неверный формат значения показателя.");
                continue;
            }
            if (value < 0)
            {
                errors.Add($"Строка {number}: значение показателя не может быть меньше 0.");
                continue;
            }

            values.Add(new ValueRecord
            {
                Date = date,
                ExecutionTime = execTime,
                Value = value
            });
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Файл невалиден:\n{string.Join("\n", errors)}");
        }

        return values;
    }

    private static bool IsHeader(string line)
    {
        var header = line.Split(';');
        return header.Length == 3
               && header[0].Trim().Equals("Date", StringComparison.OrdinalIgnoreCase)
               && header[1].Trim().Equals("ExecutionTime", StringComparison.OrdinalIgnoreCase)
               && header[2].Trim().Equals("Value", StringComparison.OrdinalIgnoreCase);
    }

    private static ResultRecord ComputeResult(List<ValueRecord> values)
    {
        var minDate = values.Min(v => v.Date);
        var maxDate = values.Max(v => v.Date);

        var sortedValues = values.Select(v => v.Value).OrderBy(x => x).ToList();
        double median;
        int count = sortedValues.Count;
        if (count % 2 == 0)
        {
            median = (sortedValues[count / 2 - 1] + sortedValues[count / 2]) / 2.0;
        }
        else
        {
            median = sortedValues[count / 2];
        }

        return new ResultRecord
        {
            DeltaTimeSeconds = (maxDate - minDate).TotalSeconds,
            MinDate = minDate,
            AvgExecutionTime = values.Average(v => v.ExecutionTime),
            AvgValue = values.Average(v => v.Value),
            MedianValue = median,
            MaxValue = values.Max(v => v.Value),
            MinValue = values.Min(v => v.Value)
        };
    }
}
