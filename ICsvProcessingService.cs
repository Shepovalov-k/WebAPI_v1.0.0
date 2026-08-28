using TimescaleApi.DTOs;

namespace TimescaleApi.Services;

public interface ICsvProcessingService
{
    Task<CsvUploadResult> ProcessCsvAsync(IFormFile file, CancellationToken cancellationToken = default);
}