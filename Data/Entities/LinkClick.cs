namespace Skinbloom.Api.Data.Entities
{
    public class LinkClick
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string LinkName { get; set; } = string.Empty;
        public string LinkUrl { get; set; } = string.Empty;
        public DateTime ClickedAt { get; set; } = DateTime.UtcNow;
        public string? SessionId { get; set; }
        public string? ReferrerUrl { get; set; }
    }
}
