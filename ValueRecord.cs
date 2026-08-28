namespace TimescaleApi.Models;

public class ValueRecord
{
    public long Id { get; set; }
    public int FileId { get; set; }
    public FileRecord File { get; set; } = null!;
    public DateTimeOffset Date { get; set; }
    public double ExecutionTime { get; set; }
    public double Value { get; set; }
}