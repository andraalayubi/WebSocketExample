namespace WebSocketExample.Models
{
    public class Document
    {
        public int Id { get; set; }
        public required string Title { get; set; }
        public required string Content { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
