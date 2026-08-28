namespace TimescaleApi.DTOs;

public class ValueDto
{
    public DateTimeOffset Date { get; set; }
    public double ExecutionTime { get; set; }
    public double Value { get; set; }
}