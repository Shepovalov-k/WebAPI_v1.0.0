using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TimescaleApi.Data;
using TimescaleApi.DTOs;
using TimescaleApi.Models;

namespace TimescaleApi.Services;

public class CsvProcessingService : ICsvProcessingService
{
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
        var fileRecord = await _dbContext.Files
            .FirstOrDefaultAsync(f => f.FileName == fileName, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (fileRecord == null)
            {
                fileRecord = new FileRecord { FileName = fileName };
                _dbContext.Files.Add(fileRecord);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var oldValues = _dbContext.Values.Where(v => v.FileId == fileRecord.Id);
                _dbContext.Values.RemoveRange(oldValues);

                var oldResult = await _dbContext.Results
                    .FirstOrDefaultAsync(r => r.FileId == fileRecord.Id, cancellationToken);
                if (oldResult != null)
                    _dbContext.Results.Remove(oldResult);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            foreach (var value in parsedValues)
            {
                value.FileId = fileRecord.Id;
            }
            await _dbContext.Values.AddRangeAsync(parsedValues, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var result = ComputeResult(parsedValues);
            result.FileId = fileRecord.Id;
            await _dbContext.Results.AddAsync(result, cancellationToken);
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
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<List<ValueRecord>> ParseAndValidateFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            throw new InvalidDataException("Файл пустой.");

        var lines = new List<string>();
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines.Add(await reader.ReadLineAsync() ?? string.Empty);
            }
        }

        if (lines.Count == 0)
            throw new InvalidDataException("Файл не содержит строк.");

        var header = lines[0].Split(';');
        bool hasHeader = header.Length == 3
                         && header[0].Trim().Equals("Date", StringComparison.OrdinalIgnoreCase)
                         && header[1].Trim().Equals("ExecutionTime", StringComparison.OrdinalIgnoreCase)
                         && header[2].Trim().Equals("Value", StringComparison.OrdinalIgnoreCase);

        if (hasHeader)
            lines.RemoveAt(0);

        if (lines.Count < 1)
            throw new InvalidDataException("Количество строк не может быть меньше 1.");
        if (lines.Count > 10_000)
            throw new InvalidDataException("Количество строк не может быть больше 10 000.");

        var values = new List<ValueRecord>(lines.Count);
        var errors = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            var lineNumber = hasHeader ? i + 2 : i + 1;
            var parts = lines[i].Split(';');
            if (parts.Length != 3)
            {
                errors.Add($"Строка {lineNumber}: ожидается 3 колонки, получено {parts.Length}.");
                continue;
            }

            var dateStr = parts[0].Trim();
            var execTimeStr = parts[1].Trim();
            var valueStr = parts[2].Trim();

            if (string.IsNullOrEmpty(dateStr) || string.IsNullOrEmpty(execTimeStr) || string.IsNullOrEmpty(valueStr))
            {
                errors.Add($"Строка {lineNumber}: одно из значений отсутствует.");
                continue;
            }

            if (!DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                errors.Add($"Строка {lineNumber}: неверный формат даты.");
                continue;
            }
            date = date.ToUniversalTime();

            if (date < MinAllowedDate)
            {
                errors.Add($"Строка {lineNumber}: дата не может быть раньше 01.01.2000.");
                continue;
            }
            if (date > DateTimeOffset.UtcNow)
            {
                errors.Add($"Строка {lineNumber}: дата не может быть позже текущей.");
                continue;
            }

            if (!double.TryParse(execTimeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var execTime))
            {
                errors.Add($"Строка {lineNumber}: неверный формат времени выполнения.");
                continue;
            }
            if (execTime < 0)
            {
                errors.Add($"Строка {lineNumber}: время выполнения не может быть меньше 0.");
                continue;
            }

            if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                errors.Add($"Строка {lineNumber}: неверный формат значения показателя.");
                continue;
            }
            if (value < 0)
            {
                errors.Add($"Строка {lineNumber}: значение показателя не может быть меньше 0.");
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

    private ResultRecord ComputeResult(List<ValueRecord> values)
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