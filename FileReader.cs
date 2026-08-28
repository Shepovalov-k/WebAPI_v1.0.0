namespace TimescaleApi.Models;

public class FileRecord
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public ICollection<ValueRecord> Values { get; set; } = new List<ValueRecord>();
    public ResultRecord? Result { get; set; }
}