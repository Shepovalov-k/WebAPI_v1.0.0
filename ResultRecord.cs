namespace TimescaleApi.Models;

public class ResultRecord
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public FileRecord File { get; set; } = null!;
    public double DeltaTimeSeconds { get; set; }
    public DateTimeOffset MinDate { get; set; }
    public double AvgExecutionTime { get; set; }
    public double AvgValue { get; set; }
    public double MedianValue { get; set; }
    public double MaxValue { get; set; }
    public double MinValue { get; set; }
}