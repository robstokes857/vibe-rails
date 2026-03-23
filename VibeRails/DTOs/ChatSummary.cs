namespace VibeRails.DTOs
{
    public class ChatSummary
    {
        public int Id { get; set; }
        public string SessionId { get; set; } = "";
        public string SummaryText { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.UtcNow;
    }
}
