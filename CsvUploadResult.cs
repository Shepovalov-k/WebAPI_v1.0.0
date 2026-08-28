namespace TimescaleApi.DTOs;

public class CsvUploadResult
{
    public string FileName { get; set; } = string.Empty;
    public int RowsProcessed { get; set; }
    public ResultDto Result { get; set; } = null!;
}