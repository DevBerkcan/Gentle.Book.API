// Data/GentleBookDbContext.cs
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Data;

public class GentleBookDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;
    public Guid? CurrentTenantId => _tenantContext?.IsSuperAdmin == true ? null : _tenantContext?.TenantId;

    public GentleBookDbContext(DbContextOptions<GentleBookDbContext> options, ITenantContext? tenantContext = null)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    // ── Platform-level ────────────────────────────────────────
    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantSettings> TenantSettings { get; set; }
    public DbSet<Subscription> Subscriptions { get; set; }
    public DbSet<PlatformUser> PlatformUsers { get; set; }
    public DbSet<BusinessLocation> BusinessLocations { get; set; }

    // ── Tenant-scoped ─────────────────────────────────────────
    public DbSet<Employee> Employees { get; set; }
    public DbSet<Service> Services { get; set; }
    public DbSet<ServiceCategory> ServiceCategories { get; set; }
    public DbSet<ServiceEmployee> ServiceEmployees { get; set; }
    public DbSet<Booking> Bookings { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<BusinessHours> BusinessHours { get; set; }
    public DbSet<EmployeeSchedule> EmployeeSchedules { get; set; }
    public DbSet<BlockedTimeSlot> BlockedTimeSlots { get; set; }
    public DbSet<EmailLog> EmailLogs { get; set; }
    public DbSet<PageView> PageViews { get; set; }
    public DbSet<LinkClick> LinkClicks { get; set; }
    public DbSet<TenantLink> TenantLinks { get; set; }
    public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }
    public DbSet<TrialAccessInvitation> TrialAccessInvitations { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<EmployeeVacation> EmployeeVacations { get; set; }
    public DbSet<SubscriptionRequest> SubscriptionRequests { get; set; }
    public DbSet<EmployeeNote> EmployeeNotes { get; set; }
    public DbSet<WaitlistEntry> WaitlistEntries { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<MollieWebhookEvent> MollieWebhookEvents { get; set; }
    public DbSet<Invoice> Invoices { get; set; }
    public DbSet<PlanPrice> PlanPrices { get; set; }
    public DbSet<IndustryProfile> IndustryProfiles { get; set; }
    public DbSet<IndustryCapability> IndustryCapabilities { get; set; }
    public DbSet<TenantIndustrySetting> TenantIndustrySettings { get; set; }
    public DbSet<TenantIndustryCapability> TenantIndustryCapabilities { get; set; }
    public DbSet<ServiceFinderQuestion> ServiceFinderQuestions { get; set; }
    public DbSet<ServiceFinderRule> ServiceFinderRules { get; set; }
    public DbSet<ServiceGuidance> ServiceGuidances { get; set; }
    public DbSet<ServiceFinderBookingDraft> ServiceFinderBookingDrafts { get; set; }
    public DbSet<AiKnowledgeSource> AiKnowledgeSources { get; set; }
    public DbSet<AiKnowledgeDocument> AiKnowledgeDocuments { get; set; }
    public DbSet<AiKnowledgeChunk> AiKnowledgeChunks { get; set; }
    public DbSet<AiConversation> AiConversations { get; set; }
    public DbSet<AiMessage> AiMessages { get; set; }
    public DbSet<AiAction> AiActions { get; set; }
    public DbSet<AiUsage> AiUsages { get; set; }
    public DbSet<BrandImportJob> BrandImportJobs { get; set; }
    public DbSet<BrandImportResult> BrandImportResults { get; set; }
    public DbSet<BrandThemeProposal> BrandThemeProposals { get; set; }
    public DbSet<BrandAssetCandidate> BrandAssetCandidates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Global Query Filters (Tenant Isolation) ───────────
        // The tenant value is read at query time. This is important because the
        // DbContext can be constructed before TenantMiddleware sets ITenantContext.
        modelBuilder.Entity<Employee>().HasQueryFilter(e => CurrentTenantId == null || e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Service>().HasQueryFilter(s => CurrentTenantId == null || s.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceCategory>().HasQueryFilter(sc => CurrentTenantId == null || sc.TenantId == CurrentTenantId);
        modelBuilder.Entity<Booking>().HasQueryFilter(b => CurrentTenantId == null || b.TenantId == CurrentTenantId);
        modelBuilder.Entity<Customer>().HasQueryFilter(c => CurrentTenantId == null || c.TenantId == CurrentTenantId);
        modelBuilder.Entity<BusinessHours>().HasQueryFilter(bh => CurrentTenantId == null || bh.TenantId == CurrentTenantId);
        modelBuilder.Entity<BlockedTimeSlot>().HasQueryFilter(bt => CurrentTenantId == null || bt.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmailLog>().HasQueryFilter(el => CurrentTenantId == null || el.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantLink>().HasQueryFilter(tl => CurrentTenantId == null || tl.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmployeeSchedule>().HasQueryFilter(es => CurrentTenantId == null || es.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmployeeVacation>().HasQueryFilter(ev => CurrentTenantId == null || ev.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmployeeNote>().HasQueryFilter(en => CurrentTenantId == null || en.TenantId == CurrentTenantId);
        modelBuilder.Entity<WaitlistEntry>().HasQueryFilter(w => CurrentTenantId == null || w.TenantId == CurrentTenantId);
        modelBuilder.Entity<BusinessLocation>().HasQueryFilter(l => CurrentTenantId == null || l.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantIndustrySetting>().HasQueryFilter(s => CurrentTenantId == null || s.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantIndustryCapability>().HasQueryFilter(s => CurrentTenantId == null || s.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceFinderQuestion>().HasQueryFilter(q => CurrentTenantId == null || q.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceFinderRule>().HasQueryFilter(r => CurrentTenantId == null || r.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceGuidance>().HasQueryFilter(g => CurrentTenantId == null || g.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceFinderBookingDraft>().HasQueryFilter(d => CurrentTenantId == null || d.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiKnowledgeSource>().HasQueryFilter(s => CurrentTenantId == null || s.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiKnowledgeDocument>().HasQueryFilter(d => CurrentTenantId == null || d.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiKnowledgeChunk>().HasQueryFilter(c => CurrentTenantId == null || c.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiConversation>().HasQueryFilter(c => CurrentTenantId == null || c.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiAction>().HasQueryFilter(a => CurrentTenantId == null || a.TenantId == CurrentTenantId);
        modelBuilder.Entity<AiUsage>().HasQueryFilter(u => CurrentTenantId == null || u.TenantId == CurrentTenantId);
        modelBuilder.Entity<BrandImportJob>().HasQueryFilter(j => CurrentTenantId == null || j.TenantId == CurrentTenantId);
        modelBuilder.Entity<BrandImportResult>().HasQueryFilter(r => CurrentTenantId == null || r.TenantId == CurrentTenantId);
        modelBuilder.Entity<BrandThemeProposal>().HasQueryFilter(p => CurrentTenantId == null || p.TenantId == CurrentTenantId);
        modelBuilder.Entity<BrandAssetCandidate>().HasQueryFilter(a => CurrentTenantId == null || a.TenantId == CurrentTenantId);

        modelBuilder.Entity<BrandImportJob>(entity =>
        {
            entity.Property(j => j.SourceUrl).HasMaxLength(2048);
            entity.Property(j => j.ErrorCode).HasMaxLength(100);
            entity.Property(j => j.ErrorMessageSafe).HasMaxLength(500);
            entity.HasIndex(j => new { j.TenantId, j.Status });
            entity.HasIndex(j => new { j.TenantId, j.SourceUrl });
        });

        modelBuilder.Entity<BrandImportResult>(entity =>
        {
            entity.Property(r => r.WebsiteTitle).HasMaxLength(300);
            entity.Property(r => r.BrandStyle).HasMaxLength(100);
            entity.HasIndex(r => new { r.TenantId, r.JobId });
        });

        modelBuilder.Entity<BrandThemeProposal>(entity =>
        {
            entity.Property(p => p.ProposalKey).HasMaxLength(50);
            entity.Property(p => p.Name).HasMaxLength(150);
            entity.Property(p => p.TemplateId).HasMaxLength(50);
            entity.HasIndex(p => new { p.TenantId, p.ImportResultId });
        });

        modelBuilder.Entity<BrandAssetCandidate>(entity =>
        {
            entity.Property(a => a.SourceUrl).HasMaxLength(2048);
            entity.Property(a => a.DiscoveryHint).HasMaxLength(200);
            entity.Property(a => a.ContentType).HasMaxLength(100);
            entity.HasIndex(a => new { a.TenantId, a.ImportResultId });
        });

        modelBuilder.Entity<WaitlistEntry>(entity =>
        {
            entity.Property(w => w.ReservationToken).HasMaxLength(128);
            entity.HasIndex(w => new { w.TenantId, w.Status, w.PreferredDate });
            entity.HasIndex(w => w.ReservationToken);
        });

        // ── AuditLog (kein Tenant-Filter: SuperAdmin liest plattformweit,
        //    Schreibzugriffe setzen TenantId explizit) ─────────
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.Property(a => a.ActorType).HasMaxLength(20);
            entity.Property(a => a.ActorName).HasMaxLength(300);
            entity.Property(a => a.Action).HasMaxLength(100);
            entity.Property(a => a.EntityType).HasMaxLength(100);
            entity.Property(a => a.EntityId).HasMaxLength(100);
            entity.Property(a => a.Details).HasMaxLength(2000);
            entity.Property(a => a.IpAddress).HasMaxLength(64);
            entity.HasIndex(a => new { a.TenantId, a.CreatedAt });
            entity.HasIndex(a => a.Action);
        });

        // ── Tenant ────────────────────────────────────────────
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.IndustryType).HasConversion<string>().HasMaxLength(50);
        });

        // ── TenantSettings ────────────────────────────────────
        modelBuilder.Entity<TenantSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithOne(t => t.Settings)
                  .HasForeignKey<TenantSettings>(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.CompanyName).HasMaxLength(200);
            entity.Property(e => e.PrimaryColor).HasMaxLength(20);
            entity.Property(e => e.SecondaryColor).HasMaxLength(20);
            entity.Property(e => e.AccentColor).HasMaxLength(20);
            entity.Property(e => e.DefaultCurrency).HasMaxLength(3).HasDefaultValue("EUR");
            entity.Property(e => e.TimeZone).HasMaxLength(100).HasDefaultValue("Europe/Berlin");
        });

        modelBuilder.Entity<BusinessLocation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(150);
            entity.Property(e => e.Street).HasMaxLength(200);
            entity.Property(e => e.PostalCode).HasMaxLength(20);
            entity.Property(e => e.City).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CountryCode).IsRequired().HasMaxLength(2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.TimeZone).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.TenantId, e.IsDefault })
                  .IsUnique()
                  .HasFilter("[IsDefault] = 1");
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Locations)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Subscription ──────────────────────────────────────
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithOne(t => t.Subscription)
                  .HasForeignKey<Subscription>(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TenantId).IsUnique();
            entity.Property(e => e.Plan).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Interval).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => e.MollieCustomerId);
            entity.HasIndex(e => e.MollieSubscriptionId);
            entity.HasIndex(e => e.LastMolliePaymentId);
            entity.Property(e => e.CancelReason).HasMaxLength(500);
            entity.Property(e => e.LastFailedMolliePaymentId).HasMaxLength(64);
            entity.Property(e => e.NegotiatedMonthlyPrice).HasPrecision(10, 2);
            entity.Property(e => e.NegotiatedAnnualPrice).HasPrecision(10, 2);
            entity.HasIndex(e => e.CancelRequestedAt);
            entity.HasIndex(e => e.PastDueSince);
            entity.HasIndex(e => e.RetentionEndsAt);
            entity.Ignore(e => e.IsInTrial);
            entity.Ignore(e => e.TrialDaysRemaining);
            entity.Ignore(e => e.IsAccessAllowed);
        });

        // ── MollieWebhookEvent ────────────────────────────────
        modelBuilder.Entity<MollieWebhookEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MollieResourceId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ResultStatus).HasMaxLength(40);
            // Keyed on (id, status) rather than id alone: Mollie fires a fresh webhook call
            // every time a payment's status changes (open -> pending -> paid is normal for
            // SEPA, which settles over several days), so the SAME payment id legitimately
            // needs processing more than once as its status progresses. Only a second delivery
            // reporting the SAME status is a true duplicate. Subscription-resource webhook
            // pings are exempt (no dedup) since ProcessSubscriptionEventAsync is already
            // idempotent by design.
            entity.HasIndex(e => new { e.MollieResourceId, e.ResultStatus })
                  .IsUnique()
                  .HasFilter("[ResourceType] = 'payment' AND [ResultStatus] IS NOT NULL");
        });

        // ── Invoice ───────────────────────────────────────────
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.InvoiceNumber).IsRequired().HasMaxLength(32);
            entity.HasIndex(e => e.InvoiceNumber).IsUnique();
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.MolliePaymentId);
            entity.Property(e => e.Amount).HasPrecision(10, 2);
            entity.Property(e => e.PlanName).HasMaxLength(100);
            entity.Property(e => e.RecipientName).HasMaxLength(200);
            entity.Property(e => e.RecipientVatId).HasMaxLength(50);
            entity.Property(e => e.RecipientStreet).HasMaxLength(200);
            entity.Property(e => e.RecipientZip).HasMaxLength(20);
            entity.Property(e => e.RecipientCity).HasMaxLength(100);
            entity.Property(e => e.RecipientCountry).HasMaxLength(2);
            entity.Property(e => e.RecipientEmail).HasMaxLength(255);
            entity.Property(e => e.Currency).HasMaxLength(3);
        });

        // ── PlanPrice ─────────────────────────────────────────
        modelBuilder.Entity<PlanPrice>(entity =>
        {
            entity.HasKey(e => e.Plan);
            entity.Property(e => e.Plan).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.MonthlyPrice).HasPrecision(10, 2);
            entity.Property(e => e.AnnualPrice).HasPrecision(10, 2);
        });

        // ── PlatformUser ──────────────────────────────────────
        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.HasIndex(e => new { e.TenantId, e.Email }).IsUnique();
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(50);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Users)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade)
                  .IsRequired(false);
            // Restrict: Tenant already cascades to PlatformUsers directly and to
            // BusinessLocations directly — a cascading PlatformUser -> Location path would be
            // a second path to BusinessLocations, the same "multiple cascade paths" issue fixed
            // earlier for TrialAccessInvitations.
            entity.HasOne(e => e.Location)
                  .WithMany()
                  .HasForeignKey(e => e.LocationId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ── PasswordResetToken ────────────────────────────────
        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.TokenHash);
            entity.HasIndex(e => new { e.UserId, e.IsUsed });
            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TrialAccessInvitation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).IsRequired().HasMaxLength(200);
            entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
            entity.Property(x => x.AcceptedByName).HasMaxLength(200);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.TermsVersion).IsRequired().HasMaxLength(32);
            entity.Property(x => x.PrivacyVersion).IsRequired().HasMaxLength(32);
            entity.Property(x => x.DpaVersion).IsRequired().HasMaxLength(32);
            entity.Property(x => x.PersonalNote).HasMaxLength(1000);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AcceptedAt });
            // Restrict (not Cascade): Tenant already cascades to PlatformUsers, and PlatformUsers.UserId
            // here cascades (SetNull) into this same table — SQL Server rejects two cascade paths
            // converging on one table ("multiple cascade paths" error 1785).
            entity.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
            entity.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(20);
            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.RevokedAt });
            entity.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Employee ──────────────────────────────────────────
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Specialty).HasMaxLength(200);
            entity.Property(e => e.Tagline).HasMaxLength(60);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Employees)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AssignedLocation)
                  .WithMany()
                  .HasForeignKey(e => e.LocationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.TenantId, e.LocationId });
        });

        // ── ServiceCategory ───────────────────────────────────
        modelBuilder.Entity<ServiceCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.ServiceCategories)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Service ───────────────────────────────────────────
        modelBuilder.Entity<Service>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Price).HasPrecision(10, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("EUR");
            entity.HasOne(s => s.Tenant)
                  .WithMany(t => t.Services)
                  .HasForeignKey(s => s.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.Category)
                  .WithMany(c => c.Services)
                  .HasForeignKey(s => s.CategoryId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(s => s.Location)
                  .WithMany(l => l.Services)
                  .HasForeignKey(s => s.LocationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
            entity.HasIndex(e => new { e.TenantId, e.LocationId });
        });

        // ── ServiceEmployee (join table) ──────────────────────
        modelBuilder.Entity<ServiceEmployee>()
            .HasKey(se => new { se.ServiceId, se.EmployeeId });

        // NoAction (not Cascade) — Service and Employee both cascade from Tenant, so a
        // Cascade here would create a second delete path into ServiceEmployees alongside
        // the Employee FK below, which SQL Server rejects (multiple cascade paths).
        // Application code already removes ServiceEmployees explicitly before deleting a
        // Service (see ServiceService.cs), so this is safe.
        modelBuilder.Entity<ServiceEmployee>()
            .HasOne(se => se.Service)
            .WithMany(s => s.ServiceEmployees)
            .HasForeignKey(se => se.ServiceId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<ServiceEmployee>()
            .HasOne(se => se.Employee)
            .WithMany(e => e.ServiceEmployees)
            .HasForeignKey(se => se.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // ── Customer ──────────────────────────────────────────
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Customers)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            // Email unique per tenant (not globally)
            entity.HasIndex(e => new { e.TenantId, e.Email })
                  .HasFilter("[Email] IS NOT NULL AND [Email] <> ''");
            entity.Ignore(e => e.FullName);
        });

        // ── Booking ───────────────────────────────────────────
        modelBuilder.Entity<Booking>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Bookings)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Customer)
                  .WithMany(c => c.Bookings)
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Service)
                  .WithMany(s => s.Bookings)
                  .HasForeignKey(e => e.ServiceId)
                  .OnDelete(DeleteBehavior.Restrict);
            // Restrict (not the nullable-FK convention default of SetNull): Tenant already
            // cascades to both Bookings and BusinessLocations directly — a second automatic
            // action here would hit SQL Server's "multiple cascade paths" error, same root
            // cause as TrialAccessInvitations.TenantId earlier.
            entity.HasOne(e => e.Location)
                  .WithMany()
                  .HasForeignKey(e => e.LocationId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.TenantId, e.BookingDate });
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => new { e.TenantId, e.LocationId });
        });

        // ── BusinessHours ─────────────────────────────────────
        modelBuilder.Entity<BusinessHours>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.BusinessHours)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            // Unique DayOfWeek per tenant
            entity.HasIndex(e => new { e.TenantId, e.DayOfWeek }).IsUnique();
            entity.Ignore(e => e.DayName);
        });

        // ── EmployeeSchedule ──────────────────────────────────
        modelBuilder.Entity<EmployeeSchedule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Employee)
                  .WithMany(emp => emp.Schedules)
                  .HasForeignKey(e => e.EmployeeId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => new { e.EmployeeId, e.DayOfWeek }).IsUnique();
            entity.Ignore(e => e.DayName);
        });

        // ── EmployeeNote ───────────────────────────────────────
        modelBuilder.Entity<EmployeeNote>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EmployeeName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(4000);
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Employee)
                  .WithMany()
                  .HasForeignKey(e => e.EmployeeId)
                  .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.EmployeeId);
        });

        // ── BlockedTimeSlot ───────────────────────────────────
        modelBuilder.Entity<BlockedTimeSlot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.Reason).HasMaxLength(255);
            entity.HasIndex(e => new { e.TenantId, e.BlockDate });
        });

        // ── EmailLog ──────────────────────────────────────────
        modelBuilder.Entity<EmailLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            // NoAction — Booking also cascades from Tenant, so a direct Cascade here would
            // create a second delete path into EmailLogs (multiple cascade paths). SuperAdminController.DeleteTenant
            // already removes EmailLogs explicitly before removing the tenant.
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.Booking)
                  .WithMany(b => b.EmailLogs)
                  .HasForeignKey(e => e.BookingId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.Property(e => e.EmailType).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.RecipientEmail).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(500);
        });

        // ── PageView / LinkClick (optional TenantId) ──────────
        modelBuilder.Entity<PageView>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PageUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ReferrerUrl).HasMaxLength(500);
            entity.HasIndex(e => e.ViewedAt);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<LinkClick>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LinkName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LinkUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.SessionId).HasMaxLength(100);
            entity.Property(e => e.ReferrerUrl).HasMaxLength(500);
            entity.HasIndex(e => e.ClickedAt);
        });

        // ── TenantLink ────────────────────────────────────────
        modelBuilder.Entity<TenantLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Url).IsRequired().HasMaxLength(500);
            entity.Property(e => e.IconType).HasConversion<string>().HasMaxLength(50);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Links)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TenantId, e.DisplayOrder });
        });

        // ── SubscriptionRequest ───────────────────────────────
        modelBuilder.Entity<SubscriptionRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RequestedPlan).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Interval).HasConversion<string>().HasMaxLength(50);
            entity.Property(e => e.ContactEmail).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TenantId, e.Status });
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<IndustryProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.HasIndex(e => e.Key).IsUnique();
        });

        modelBuilder.Entity<IndustryCapability>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CapabilityKey).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.IndustryProfileId, e.CapabilityKey }).IsUnique();
            entity.HasOne(e => e.IndustryProfile)
                .WithMany(p => p.Capabilities)
                .HasForeignKey(e => e.IndustryProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantIndustrySetting>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SettingsJson).HasMaxLength(8000);
            entity.HasIndex(e => e.TenantId).IsUnique();
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.PrimaryIndustryProfile)
                .WithMany()
                .HasForeignKey(e => e.PrimaryIndustryProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantIndustryCapability>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CapabilityKey).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.TenantId, e.CapabilityKey }).IsUnique();
        });

        modelBuilder.Entity<ServiceFinderQuestion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.QuestionKey).IsRequired().HasMaxLength(120);
            entity.Property(e => e.QuestionText).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ConfigJson).HasMaxLength(8000);
            entity.Property(e => e.AnswerType).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.TenantId, e.QuestionKey }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.IsActive, e.DisplayOrder });
            entity.HasOne(e => e.IndustryProfile)
                .WithMany()
                .HasForeignKey(e => e.IndustryProfileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ServiceFinderRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RuleType).HasConversion<string>().HasMaxLength(80);
            entity.Property(e => e.ApprovalStatus).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.ConditionJson).IsRequired().HasMaxLength(8000);
            entity.Property(e => e.ResultJson).IsRequired().HasMaxLength(8000);
            entity.HasIndex(e => new { e.TenantId, e.IsActive, e.Priority });
            entity.HasOne(e => e.Service)
                .WithMany()
                .HasForeignKey(e => e.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ServiceGuidance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GuidanceType).HasConversion<string>().HasMaxLength(60);
            entity.Property(e => e.ApprovalStatus).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
            entity.Property(e => e.Content).IsRequired().HasMaxLength(8000);
            entity.HasIndex(e => new { e.TenantId, e.ServiceId, e.GuidanceType, e.IsActive });
            entity.HasOne(e => e.Service)
                .WithMany()
                .HasForeignKey(e => e.ServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceFinderBookingDraft>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(120);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.CustomerNotes).HasMaxLength(1000);
            entity.HasIndex(e => new { e.TenantId, e.Status, e.ExpiresAt });
        });

        modelBuilder.Entity<AiKnowledgeSource>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(250);
            entity.Property(e => e.OriginalFileName).HasMaxLength(255);
            entity.Property(e => e.Visibility).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ApprovalStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.HasIndex(e => new { e.TenantId, e.Visibility, e.ApprovalStatus, e.Status });
        });

        modelBuilder.Entity<AiKnowledgeDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(12);
            entity.HasIndex(e => new { e.TenantId, e.SourceId, e.IsActive });
            entity.HasOne(e => e.Source)
                .WithMany(s => s.Documents)
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiKnowledgeChunk>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SectionName).HasMaxLength(200);
            entity.Property(e => e.VectorReference).HasMaxLength(300);
            entity.Property(e => e.MetadataJson).HasMaxLength(4000);
            entity.Property(e => e.Content).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.DocumentId });
            entity.HasOne(e => e.Document)
                .WithMany(d => d.Chunks)
                .HasForeignKey(e => e.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiConversation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Channel).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
        });

        modelBuilder.Entity<AiMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Content).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt });
            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiAction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActionType).IsRequired().HasMaxLength(80);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(40);
            entity.Property(e => e.InputJson).IsRequired();
            entity.Property(e => e.OutputJson).IsRequired();
            entity.HasIndex(e => new { e.TenantId, e.CreatedOn });
        });

        modelBuilder.Entity<AiUsage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Feature).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Model).IsRequired().HasMaxLength(120);
            entity.Property(e => e.EstimatedCost).HasPrecision(12, 4);
            entity.HasIndex(e => new { e.TenantId, e.Feature, e.CreatedOn });
        });
    }
}
