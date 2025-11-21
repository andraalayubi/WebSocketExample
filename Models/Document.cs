public class Document
{
    public int Id { get; set; }
    
    public byte[]? YjsState { get; set; }
    
    public string Content { get; set; } = string.Empty;
    
    public DateTime LastUpdated { get; set; }
}