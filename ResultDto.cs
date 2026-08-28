namespace TimescaleApi.DTOs;

public class ResultDto
{
    public string FileName { get; set; } = string.Empty;
    public double DeltaTimeSeconds { get; set; }
    public DateTimeOffset MinDate { get; set; }
    public double AvgExecutionTime { get; set; }
    public double AvgValue { get; set; }
    public double MedianValue { get; set; }
    public double MaxValue { get; set; }
    public double MinValue { get; set; }
}