namespace Skinbloom.Api.Data.Entities
{
    public class PageView
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string PageUrl { get; set; } = string.Empty;
        public string? ReferrerUrl { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? UtmContent { get; set; }
        public string? UtmTerm { get; set; }
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }
        public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
        public string? SessionId { get; set; }
    }
}
