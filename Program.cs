using GentleBook.Api.Data;
using GentleBook.Api.Middleware;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
        opts.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();

// ── Swagger with JWT support ──────────────────────────────────────────────────
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "GentleBook API",
        Version = "v1",
        Description = "Multi-Tenant Booking Platform API"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header. Example: \"Bearer {token}\""
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ── JWT Authentication ────────────────────────────────────────────────────────
// Accepts tokens from both the standard (tenant) issuer and the SuperAdmin issuer.
var jwtSecret = builder.Configuration["Jwt:Secret"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;
var superAdminSecret = builder.Configuration["Jwt:SuperAdminSecret"]!;
var superAdminIssuer = builder.Configuration["Jwt:SuperAdminIssuer"]!;
var superAdminAudience = builder.Configuration["Jwt:SuperAdminAudience"]!;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // Keep "role" as "role", don't remap to ClaimTypes.Role
        // Accept tokens signed with either the tenant secret or the SuperAdmin secret
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
            {
                var keys = new List<SecurityKey>
                {
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(superAdminSecret))
                };
                return keys;
            },
            ValidateIssuer = true,
            ValidIssuers = new[] { jwtIssuer, superAdminIssuer },
            ValidateAudience = true,
            ValidAudiences = new[] { jwtAudience, superAdminAudience },
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                if (!context.Response.HasStarted)
                {
                    context.HandleResponse();
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync("{\"message\":\"Unauthorized\"}");
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpClient();

// ── Database ──────────────────────────────────────────────────────────────────
// TenantContext is Scoped so it is set per-request by TenantMiddleware.
builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services.AddDbContext<GentleBookDbContext>((serviceProvider, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<BlockedTimeSlotService>();
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<ManualBookingService>();
builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<ServiceService>();
builder.Services.AddScoped<EmployeeAuthService>();
builder.Services.AddSingleton<SubscriptionService>();

// ── Hangfire ──────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();
builder.Services.AddSingleton<IHostedService, HangfireJobScheduler>();

// ── Rate Limiting ─────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    // Brute-force protection: max 8 login attempts per IP per minute
    options.AddFixedWindowLimiter("auth-limit", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 8;
        o.QueueLimit = 0;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    // Booking spam protection: max 10 bookings per IP per 10 minutes
    options.AddFixedWindowLimiter("booking-limit", o =>
    {
        o.Window = TimeSpan.FromMinutes(10);
        o.PermitLimit = 10;
        o.QueueLimit = 0;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    options.RejectionStatusCode = 429;
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            """{"message":"Zu viele Anfragen. Bitte warten Sie einen Moment."}""", ct);
    };
});

// ── CORS ──────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("GentleBookCors", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// ── Middleware Pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "GentleBook API V1");
        c.ConfigObject.AdditionalItems["persistAuthorization"] = true;
    });
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"message":"Ein interner Fehler ist aufgetreten. Bitte versuchen Sie es erneut."}""");
    });
});

app.UseCors("GentleBookCors");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// TenantMiddleware: runs after authentication, sets ITenantContext from JWT claims.
// Also validates subscription access for tenant-scoped routes.
app.UseMiddleware<TenantMiddleware>();

// ── Hangfire Jobs ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseHangfireDashboard("/hangfire");
}

using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    // Send booking reminders daily at 8:00 AM UTC
    recurringJobManager.AddOrUpdate<ReminderService>(
        "daily-reminders",
        service => service.SendDailyRemindersAsync(),
        Cron.Daily(8, 0));

    // Trial expiration: daily at 1:00 AM UTC
    recurringJobManager.AddOrUpdate<SubscriptionService>(
        "trial-expiration-check",
        service => service.ProcessExpiredTrialsAsync(),
        Cron.Daily(1, 0));
}

app.MapControllers();
app.MapGet("/health", () => "GentleBook API is running");

// ── Auto-migrate on every startup ─────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[MIGRATION ERROR] {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        // App continues even if migration fails — so we can diagnose via API
    }
}

// ── Schema-Fallback: fehlende Spalten direkt anlegen ─────────────────────────
// Falls MigrateAsync auf Production keine Rechte hat, legen wir die Spalten
// per Raw-SQL an (IF NOT EXISTS = idempotent, sicher bei mehrfachem Neustart).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'LinktreeStyle')
                ALTER TABLE TenantSettings ADD LinktreeStyle nvarchar(max) NOT NULL DEFAULT '';

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'LinktreeConfig')
                ALTER TABLE TenantSettings ADD LinktreeConfig nvarchar(max) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlatformUsers') AND name = 'MustChangePassword')
                ALTER TABLE PlatformUsers ADD MustChangePassword bit NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlatformUsers') AND name = 'PasswordResetToken')
                ALTER TABLE PlatformUsers ADD PasswordResetToken nvarchar(max) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PlatformUsers') AND name = 'PasswordResetTokenExpiry')
                ALTER TABLE PlatformUsers ADD PasswordResetTokenExpiry datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('PasswordResetTokens') AND type = 'U')
            BEGIN
                CREATE TABLE PasswordResetTokens (
                    Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                    UserId uniqueidentifier NOT NULL,
                    TokenHash nvarchar(64) NOT NULL,
                    ExpiresAt datetime2 NOT NULL,
                    IsUsed bit NOT NULL DEFAULT 0,
                    CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT PK_PasswordResetTokens PRIMARY KEY (Id),
                    CONSTRAINT FK_PasswordResetTokens_PlatformUsers FOREIGN KEY (UserId)
                        REFERENCES PlatformUsers(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_PasswordResetTokens_TokenHash ON PasswordResetTokens(TokenHash);
                CREATE INDEX IX_PasswordResetTokens_UserId_IsUsed ON PasswordResetTokens(UserId, IsUsed);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformUsers_TenantId_Email' AND object_id = OBJECT_ID('PlatformUsers'))
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlatformUsers_Email' AND object_id = OBJECT_ID('PlatformUsers'))
                    DROP INDEX IX_PlatformUsers_Email ON PlatformUsers;
                CREATE UNIQUE INDEX IX_PlatformUsers_TenantId_Email ON PlatformUsers(TenantId, Email);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Services') AND name = 'BufferTimeMinutes')
                ALTER TABLE Services ADD BufferTimeMinutes int NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'CancellationHoursNotice')
                ALTER TABLE TenantSettings ADD CancellationHoursNotice int NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenantSettings') AND name = 'CancellationFeePercent')
                ALTER TABLE TenantSettings ADD CancellationFeePercent decimal(5,2) NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'IsEmailVerified')
                ALTER TABLE Customers ADD IsEmailVerified bit NOT NULL DEFAULT 0;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'EmailVerificationToken')
                ALTER TABLE Customers ADD EmailVerificationToken nvarchar(200) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'EmailVerificationTokenExpiry')
                ALTER TABLE Customers ADD EmailVerificationTokenExpiry datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'ConsentGivenAt')
                ALTER TABLE Customers ADD ConsentGivenAt datetime2 NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('EmployeeVacations') AND type = 'U')
            BEGIN
                CREATE TABLE EmployeeVacations (
                    Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                    TenantId uniqueidentifier NOT NULL,
                    EmployeeId uniqueidentifier NOT NULL,
                    StartDate date NOT NULL,
                    EndDate date NOT NULL,
                    Type nvarchar(50) NOT NULL DEFAULT 'Vacation',
                    Note nvarchar(500) NULL,
                    CreatedAt datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    CONSTRAINT PK_EmployeeVacations PRIMARY KEY (Id),
                    CONSTRAINT FK_EmployeeVacations_Employees FOREIGN KEY (EmployeeId)
                        REFERENCES Employees(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_EmployeeVacations_EmployeeId ON EmployeeVacations(EmployeeId);
                CREATE INDEX IX_EmployeeVacations_TenantId ON EmployeeVacations(TenantId);
            END
        ");
        Console.WriteLine("[SCHEMA-FALLBACK] OK");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SCHEMA-FALLBACK ERROR] {ex.Message}");
    }
}

app.Run();
