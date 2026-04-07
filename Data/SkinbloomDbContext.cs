// Data/GentleBookDbContext.cs
using GentleBook.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Data;

public class GentleBookDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;

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

    // ── Tenant-scoped ─────────────────────────────────────────
    public DbSet<Employee> Employees { get; set; }
    public DbSet<Service> Services { get; set; }
    public DbSet<ServiceCategory> ServiceCategories { get; set; }
    public DbSet<ServiceEmployee> ServiceEmployees { get; set; }
    public DbSet<Booking> Bookings { get; set; }
    public DbSet<Customer> Customers { get; set; }
    public DbSet<BusinessHours> BusinessHours { get; set; }
    public DbSet<BlockedTimeSlot> BlockedTimeSlots { get; set; }
    public DbSet<EmailLog> EmailLogs { get; set; }
    public DbSet<PageView> PageViews { get; set; }
    public DbSet<LinkClick> LinkClicks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Global Query Filters (Tenant Isolation) ───────────
        // Applied automatically when TenantId is available in context.
        // SuperAdmin bypasses these via IgnoreQueryFilters().
        var tenantId = _tenantContext?.TenantId;

        if (tenantId.HasValue)
        {
            modelBuilder.Entity<Employee>().HasQueryFilter(e => e.TenantId == tenantId.Value);
            modelBuilder.Entity<Service>().HasQueryFilter(s => s.TenantId == tenantId.Value);
            modelBuilder.Entity<ServiceCategory>().HasQueryFilter(sc => sc.TenantId == tenantId.Value);
            modelBuilder.Entity<Booking>().HasQueryFilter(b => b.TenantId == tenantId.Value);
            modelBuilder.Entity<Customer>().HasQueryFilter(c => c.TenantId == tenantId.Value);
            modelBuilder.Entity<BusinessHours>().HasQueryFilter(bh => bh.TenantId == tenantId.Value);
            modelBuilder.Entity<BlockedTimeSlot>().HasQueryFilter(bt => bt.TenantId == tenantId.Value);
            modelBuilder.Entity<EmailLog>().HasQueryFilter(el => el.TenantId == tenantId.Value);
        }

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
            entity.Ignore(e => e.IsInTrial);
            entity.Ignore(e => e.TrialDaysRemaining);
            entity.Ignore(e => e.IsAccessAllowed);
        });

        // ── PlatformUser ──────────────────────────────────────
        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(50);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Users)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade)
                  .IsRequired(false);
        });

        // ── Employee ──────────────────────────────────────────
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Specialty).HasMaxLength(200);
            entity.HasOne(e => e.Tenant)
                  .WithMany(t => t.Employees)
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
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
            entity.HasIndex(e => new { e.TenantId, e.IsActive });
        });

        // ── ServiceEmployee (join table) ──────────────────────
        modelBuilder.Entity<ServiceEmployee>()
            .HasKey(se => new { se.ServiceId, se.EmployeeId });

        modelBuilder.Entity<ServiceEmployee>()
            .HasOne(se => se.Service)
            .WithMany(s => s.ServiceEmployees)
            .HasForeignKey(se => se.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

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
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(e => new { e.TenantId, e.BookingDate });
            entity.HasIndex(e => new { e.TenantId, e.Status });
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
            entity.HasOne(e => e.Tenant)
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);
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
    }
}
