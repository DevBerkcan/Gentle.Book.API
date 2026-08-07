using GentleBook.Api.Data;
using GentleBook.Api.Middleware;
using GentleBook.Api.Options;
using GentleBook.Api.Services;
using GentleBook.Api.Services.AI;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;

// Global last-resort diagnostic: catches truly unhandled exceptions anywhere in the process
// (including background threads) that would otherwise crash silently with no trace on this
// host's unreliable stdout capture.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "crash-diagnostic.txt"),
            $"{DateTime.UtcNow:O} AppDomain.UnhandledException (IsTerminating={e.IsTerminating})\n{e.ExceptionObject}");
    }
    catch { /* best effort */ }
};

var builder = WebApplication.CreateBuilder(args);

try
{
    var earlyDiagDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(earlyDiagDir);
    File.WriteAllText(Path.Combine(earlyDiagDir, "checkpoint.txt"), $"{DateTime.UtcNow:O} BUILDER_CREATED\n");
}
catch { /* best effort */ }

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
var tenantSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
            var superAdminSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(superAdminSecret));

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
                {
                    try
                    {
                        var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
                        return jwtToken.Issuer switch
                        {
                            var issuer when issuer == superAdminIssuer => new[] { superAdminSigningKey },
                            var issuer when issuer == jwtIssuer => new[] { tenantSigningKey },
                            _ => Array.Empty<SecurityKey>()
                        };
                    }
                    catch
                    {
                        return Array.Empty<SecurityKey>();
                    }
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
    })
    // Agency-exclusive public API (Controllers/PublicApiV1Controller.cs) — only used by
    // controllers explicitly marked [Authorize(AuthenticationSchemes = "ApiKey")]; the default
    // scheme above stays JWT Bearer for every other [Authorize] endpoint.
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { });

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
builder.Services.Configure<InvoiceEmailOptions>(builder.Configuration.GetSection("InvoiceEmail"));
builder.Services.Configure<MollieOptions>(builder.Configuration.GetSection("Mollie"));
builder.Services.Configure<CrmOptions>(builder.Configuration.GetSection("Crm"));
builder.Services.Configure<AiProviderOptions>(builder.Configuration.GetSection("Ai"));

// Mollie: first real outbound-HTTP integration in this codebase.
builder.Services.AddHttpClient<MollieClient>((sp, client) =>
{
    var mollieBaseUrl = sp.GetRequiredService<IOptions<MollieOptions>>().Value.BaseUrl;
    client.BaseAddress = new Uri(mollieBaseUrl);
});

// CRM push: second typed HttpClient, points at the Gentle.Suite CRM's integration endpoint.
builder.Services.AddHttpClient<CrmPushService>((sp, client) =>
{
    var crmBaseUrl = sp.GetRequiredService<IOptions<CrmOptions>>().Value.BaseUrl;
    if (!string.IsNullOrEmpty(crmBaseUrl))
        client.BaseAddress = new Uri(crmBaseUrl.TrimEnd('/') + "/");
});

// OpenAI: third typed HttpClient — one platform-wide key (see IAiProviderAdapter registration
// below), never per-tenant. Falls back to the real OpenAI base URL when "Ai:BaseUrl" is unset,
// so the only required config to go live is "Ai:ApiKey".
builder.Services.AddHttpClient<GentleBook.Api.Services.AI.OpenAiClient>((sp, client) =>
{
    var aiBaseUrl = sp.GetRequiredService<IOptions<AiProviderOptions>>().Value.BaseUrl;
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(aiBaseUrl) ? "https://api.openai.com/v1/" : aiBaseUrl.TrimEnd('/') + "/");
});

builder.Services.AddScoped<MollieService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddSingleton<MollieReconciliationJob>();

builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<BlockedTimeSlotService>();
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<WaitlistService>();
builder.Services.AddScoped<LoyaltyService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<BookingCompletionService>();
builder.Services.AddScoped<ReviewRequestService>();
builder.Services.AddScoped<AdminDigestService>();
builder.Services.AddScoped<VoucherService>();
builder.Services.AddScoped<ManualBookingService>();
builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<ServiceService>();
builder.Services.AddScoped<EmployeeAuthService>();
builder.Services.AddScoped<CustomerPortalTokenService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<IServiceFinderEngine, ServiceFinderEngine>();
// Real OpenAI-backed adapter when a platform API key is configured (Ai:ApiKey), otherwise the
// deterministic Null adapter — same "never hard-fail on missing config" convention as
// IBrandAiAnalyzer below. AiOrchestrator additionally gates the real adapter to Agency-plan
// tenants only, so even with a key configured, Trial/Starter/Professional never call out.
var aiApiKeyConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Ai:ApiKey"]);
if (aiApiKeyConfigured)
    builder.Services.AddScoped<IAiProviderAdapter, GentleBook.Api.Services.AI.OpenAiProviderAdapter>();
else
    builder.Services.AddScoped<IAiProviderAdapter, NullAiProviderAdapter>();
builder.Services.AddScoped<IAiUsageMeter, AiUsageMeter>();
builder.Services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();
builder.Services.AddScoped<IAiOrchestrator, AiOrchestrator>();
builder.Services.AddScoped<IBookingDraftService, BookingDraftService>();
builder.Services.AddScoped<IAiToolRegistry, AiToolRegistry>();

// AI Brand Import: admin enters an existing company website URL and GentleBook derives a
// branding draft (colors/fonts/logo/template) for the booking-hub page. UrlSecurityValidator +
// SafeWebsiteFetcher are the SSRF-protection layer — see Services/BrandImport for details.
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IUrlSecurityValidator, GentleBook.Api.Services.BrandImport.UrlSecurityValidator>();
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.ISafeWebsiteFetcher, GentleBook.Api.Services.BrandImport.SafeWebsiteFetcher>();
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IHtmlBrandExtractor, GentleBook.Api.Services.BrandImport.HtmlBrandExtractor>();
// No real AI provider is wired in yet (see IAiProviderAdapter above) — DeterministicBrandAnalyzer
// is the always-available, no-AI fallback path described in the feature spec.
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IBrandAiAnalyzer, GentleBook.Api.Services.BrandImport.DeterministicBrandAnalyzer>();
// Services/pricing extraction reuses the same platform-wide OpenAiClient as IAiProviderAdapter —
// it self-skips (empty list) when Ai:ApiKey is unset, so no conditional registration needed here.
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IServiceListExtractor, GentleBook.Api.Services.BrandImport.LlmServiceListExtractor>();
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IWebsiteBrandAnalysisService, GentleBook.Api.Services.BrandImport.WebsiteBrandAnalysisService>();
builder.Services.AddScoped<GentleBook.Api.Services.BrandImport.IBrandImportApplyService, GentleBook.Api.Services.BrandImport.BrandImportApplyService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<SubscriptionService>();

// ── Hangfire ──────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();
builder.Services.AddSingleton<IHostedService, HangfireJobScheduler>();
builder.Services.AddScoped<NoShowService>();

// Alerts the owner by email whenever a recurring job exhausts its retries and fails for good —
// see HangfireFailureAlertFilter for why this matters (dashboard is dev-only, ILogger alone
// reaches nobody in production).
builder.Services.AddSingleton<GentleBook.Api.Services.HangfireFailureAlertFilter>();

// ── Rate Limiting ─────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(options =>
{
    // Brute-force protection: max 8 login attempts per IP per minute
    options.AddPolicy("auth-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 8,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // Booking spam protection: max 20 bookings per IP per 10 minutes
    options.AddPolicy("booking-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(10),
                PermitLimit = 20,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // Public v1 API (Agency-exclusive, api-key authenticated) — partitioned by the key itself
    // (not IP), so one integration's traffic never throttles another tenant's.
    options.AddPolicy("api-key-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers["X-Api-Key"].ToString() is { Length: > 0 } apiKey
                ? apiKey
                : (httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous"),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 60,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // AI Finder abuse/cost protection: max 15 evaluate/booking-draft calls per IP per 10 minutes.
    // Anonymous + triggers the AI orchestrator (real cost once a real provider is wired in), so it
    // gets its own tighter policy rather than reusing booking-limit's 20/10min.
    options.AddPolicy("finder-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(10),
                PermitLimit = 15,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // Brand Import abuse protection: each analyze/reanalyze call fetches an external website —
    // partitioned per authenticated tenant (not IP) since this is an admin-only, authenticated
    // feature. Max 6 analyses per tenant per 10 minutes.
    options.AddPolicy("brand-import-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("tenantId")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(10),
                PermitLimit = 6,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

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

// Diagnostic checkpoints — bypass IIS/ANCM stdout capture entirely (proven unreliable for
// early startup failures on this host) so we can see exactly how far startup gets even if
// nothing else survives to write a log. Harmless no-op once startup is actually healthy.
try
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "logs", "checkpoint.txt"), $"{DateTime.UtcNow:O} PRE_BUILD\n");
}
catch { /* best effort */ }

var app = builder.Build();

try
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "logs", "checkpoint.txt"), $"{DateTime.UtcNow:O} BUILD_OK\n");
}
catch { /* best effort */ }

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
Hangfire.GlobalJobFilters.Filters.Add(app.Services.GetRequiredService<GentleBook.Api.Services.HangfireFailureAlertFilter>());

if (app.Environment.IsDevelopment())
{
    // Dashboard nur in Development UND nur für lokale Requests — verhindert
    // offenen Zugriff, falls Production versehentlich mit Development-Env läuft.
    app.UseHangfireDashboard("/hangfire", new Hangfire.DashboardOptions
    {
        Authorization = new[] { new LocalOnlyDashboardAuthorizationFilter() }
    });
}

// Recurring Hangfire jobs are registered by HangfireJobScheduler (IHostedService, see
// builder.Services.AddSingleton<IHostedService, HangfireJobScheduler>() above). Registering
// them here too duplicated "process-expired-trials" under a second job id
// ("trial-expiration-check"), causing it to run twice a day — removed. Hangfire persists
// recurring jobs in its own storage, so the stale id must be explicitly deleted once,
// otherwise it keeps firing even though the code above no longer re-registers it.
using (var scope = app.Services.CreateScope())
{
    try
    {
        scope.ServiceProvider.GetRequiredService<IRecurringJobManager>()
            .RemoveIfExists("trial-expiration-check");
    }
    catch (Exception ex)
    {
        // A transient Hangfire/SQL hiccup here must never take the whole app down —
        // same resilience contract as the auto-migration block below.
        Console.Error.WriteLine($"[HANGFIRE CLEANUP ERROR] {ex.Message}");
    }
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

// Seed default AI industry playbooks. This is idempotent and only creates missing keys.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        var now = DateTime.UtcNow;
        var requiredProfiles = new[]
        {
            new
            {
                Key = "beauty_aesthetics",
                Name = "Beauty und Asthetik",
                Description = "Playbook fur Beauty-, Kosmetik-, Nails- und Lash-Studios.",
                Capabilities = new[]
                {
                    "staff", "consultation", "serviceSeries", "rebooking", "deposits",
                    "beforeAfterPhotos", "consentForms", "preparationGuidance", "aftercareGuidance", "healthInformation"
                }
            },
            new
            {
                Key = "barbershop_hairdresser",
                Name = "Barbershop und Friseur",
                Description = "Playbook fur Barber- und Friseurbetriebe.",
                Capabilities = new[]
                {
                    "staff", "portfolio", "referenceImages", "rebooking", "rooms", "equipment", "preparationGuidance"
                }
            },
            new
            {
                Key = "tattoo",
                Name = "Tattoo",
                Description = "Playbook fur Tattoo- und Piercingstudios.",
                Capabilities = new[]
                {
                    "staff", "referenceImages", "consultation", "deposits", "aftercareGuidance", "multipleSessions", "consentForms"
                }
            }
        };

        foreach (var profileSeed in requiredProfiles)
        {
            var profile = await db.IndustryProfiles.FirstOrDefaultAsync(p => p.Key == profileSeed.Key);
            if (profile == null)
            {
                profile = new GentleBook.Api.Data.Entities.IndustryProfile
                {
                    Key = profileSeed.Key,
                    Name = profileSeed.Name,
                    Description = profileSeed.Description,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.IndustryProfiles.Add(profile);
                await db.SaveChangesAsync();
            }

            var existing = await db.IndustryCapabilities
                .Where(c => c.IndustryProfileId == profile.Id)
                .Select(c => c.CapabilityKey)
                .ToListAsync();

            var missing = profileSeed.Capabilities
                .Where(c => !existing.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var capability in missing)
            {
                db.IndustryCapabilities.Add(new GentleBook.Api.Data.Entities.IndustryCapability
                {
                    IndustryProfileId = profile.Id,
                    CapabilityKey = capability,
                    DefaultEnabled = true
                });
            }
        }

        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[AI PLAYBOOK SEED ERROR] {ex.Message}");
    }
}

// Load any SuperAdmin-edited plan prices into PlanLimits' in-memory cache. Rows are only
// written when a price is actually changed, so absence here just means "use the compiled default".
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        var prices = await db.PlanPrices.AsNoTracking().ToListAsync();
        foreach (var p in prices)
            GentleBook.Api.Configuration.PlanLimits.SetPrice(p.Plan, p.MonthlyPrice, p.AnnualPrice);
        Console.WriteLine($"[PLAN-PRICING] Loaded {prices.Count} price override(s).");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[PLAN-PRICING LOAD ERROR] {ex.Message}");
    }
}

// Normalize legacy service rows that still carry a currency different from the
// tenant-wide setting. This is idempotent and also fixes existing installations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        var tenantCurrencies = await db.TenantSettings
            .AsNoTracking()
            .Select(settings => new { settings.TenantId, settings.DefaultCurrency })
            .ToListAsync();

        foreach (var tenant in tenantCurrencies)
        {
            var currency = tenant.DefaultCurrency.Trim().ToUpperInvariant();
            if (currency.Length != 3) continue;

            await db.Services
                .Where(service => service.TenantId == tenant.TenantId
                    && service.LocationId == null
                    && service.Currency != currency)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(service => service.Currency, currency)
                    .SetProperty(service => service.UpdatedAt, DateTime.UtcNow));
        }

        var locations = await db.BusinessLocations
            .AsNoTracking()
            .Select(location => new { location.Id, location.Currency })
            .ToListAsync();
        foreach (var location in locations)
        {
            var currency = location.Currency.Trim().ToUpperInvariant();
            await db.Services
                .Where(service => service.LocationId == location.Id && service.Currency != currency)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(service => service.Currency, currency)
                    .SetProperty(service => service.UpdatedAt, DateTime.UtcNow));
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[CURRENCY NORMALIZATION ERROR] {ex.Message}");
    }
}

// One-time fix for a bug where AuthController.ResetPassword never cleared
// MustChangePassword after a successful reset (unlike ChangePassword, which
// always did). This left every admin who ever used the setup/invite link stuck
// with MustChangePassword=true forever, re-triggering the settings redirect on
// every login even though they'd already completed setup. Safe to run on every
// startup: "has a used PasswordResetToken" only ever becomes true once that user
// has actually completed a real reset, so this never clears the flag for someone
// who still genuinely needs to set their initial password.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GentleBookDbContext>();
    try
    {
        var usersWithUsedResetToken = await db.PasswordResetTokens
            .Where(t => t.IsUsed)
            .Select(t => t.UserId)
            .Distinct()
            .ToListAsync();

        if (usersWithUsedResetToken.Count > 0)
        {
            var fixedCount = await db.PlatformUsers
                .Where(u => u.MustChangePassword && usersWithUsedResetToken.Contains(u.Id))
                .ExecuteUpdateAsync(updates => updates.SetProperty(u => u.MustChangePassword, false));

            if (fixedCount > 0)
                Console.WriteLine($"[MUST-CHANGE-PASSWORD FIX] Cleared stale flag for {fixedCount} user(s) who already completed password setup.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[MUST-CHANGE-PASSWORD FIX ERROR] {ex.Message}");
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

            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'EmployeeId' AND is_nullable = 0)
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_EmployeeId' AND object_id = OBJECT_ID('Customers'))
                    DROP INDEX IX_Customers_EmployeeId ON Customers;

                ALTER TABLE Customers ALTER COLUMN EmployeeId uniqueidentifier NULL;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_EmployeeId' AND object_id = OBJECT_ID('Customers'))
                    CREATE INDEX IX_Customers_EmployeeId ON Customers(EmployeeId);
            END

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_Email' AND object_id = OBJECT_ID('Customers'))
                DROP INDEX IX_Customers_Email ON Customers;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_TenantId_Email' AND object_id = OBJECT_ID('Customers'))
                CREATE INDEX IX_Customers_TenantId_Email ON Customers(TenantId, Email) WHERE Email IS NOT NULL AND Email <> '';

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

            IF OBJECT_ID(N'EmployeeNotes', N'U') IS NULL
            BEGIN
                CREATE TABLE EmployeeNotes (
                    Id int IDENTITY(1,1) NOT NULL,
                    TenantId uniqueidentifier NOT NULL,
                    EmployeeId uniqueidentifier NOT NULL,
                    EmployeeName nvarchar(200) NOT NULL,
                    Subject nvarchar(200) NOT NULL,
                    Message nvarchar(4000) NOT NULL,
                    IsRead bit NOT NULL CONSTRAINT DF_EmployeeNotes_IsRead DEFAULT 0,
                    CreatedAt datetime2 NOT NULL CONSTRAINT DF_EmployeeNotes_CreatedAt DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT PK_EmployeeNotes PRIMARY KEY (Id),
                    CONSTRAINT FK_EmployeeNotes_Tenants_TenantId FOREIGN KEY (TenantId)
                        REFERENCES Tenants(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_EmployeeNotes_Employees_EmployeeId FOREIGN KEY (EmployeeId)
                        REFERENCES Employees(Id) ON DELETE NO ACTION
                );
                CREATE INDEX IX_EmployeeNotes_TenantId ON EmployeeNotes(TenantId);
                CREATE INDEX IX_EmployeeNotes_EmployeeId ON EmployeeNotes(EmployeeId);
            END

            IF OBJECT_ID(N'WaitlistEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE WaitlistEntries (
                    Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                    TenantId uniqueidentifier NOT NULL,
                    ServiceId uniqueidentifier NULL,
                    EmployeeId uniqueidentifier NULL,
                    PreferredDate date NULL,
                    FirstName nvarchar(max) NOT NULL,
                    LastName nvarchar(max) NOT NULL,
                    Email nvarchar(max) NOT NULL,
                    Phone nvarchar(max) NULL,
                    Notes nvarchar(max) NULL,
                    Status int NOT NULL CONSTRAINT DF_WaitlistEntries_Status DEFAULT 0,
                    CreatedAt datetime2 NOT NULL CONSTRAINT DF_WaitlistEntries_CreatedAt DEFAULT SYSUTCDATETIME(),
                    NotifiedAt datetime2 NULL,
                    CONSTRAINT PK_WaitlistEntries PRIMARY KEY (Id),
                    CONSTRAINT FK_WaitlistEntries_Tenants_TenantId FOREIGN KEY (TenantId)
                        REFERENCES Tenants(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_WaitlistEntries_Services_ServiceId FOREIGN KEY (ServiceId)
                        REFERENCES Services(Id),
                    CONSTRAINT FK_WaitlistEntries_Employees_EmployeeId FOREIGN KEY (EmployeeId)
                        REFERENCES Employees(Id)
                );
                CREATE INDEX IX_WaitlistEntries_TenantId ON WaitlistEntries(TenantId);
                CREATE INDEX IX_WaitlistEntries_ServiceId ON WaitlistEntries(ServiceId);
                CREATE INDEX IX_WaitlistEntries_EmployeeId ON WaitlistEntries(EmployeeId);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'PreferredStartTime')
                ALTER TABLE WaitlistEntries ADD PreferredStartTime time NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'PreferredEndTime')
                ALTER TABLE WaitlistEntries ADD PreferredEndTime time NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservationToken')
                ALTER TABLE WaitlistEntries ADD ReservationToken nvarchar(128) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservationExpiresAt')
                ALTER TABLE WaitlistEntries ADD ReservationExpiresAt datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedStartTime')
                ALTER TABLE WaitlistEntries ADD ReservedStartTime time NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedEndTime')
                ALTER TABLE WaitlistEntries ADD ReservedEndTime time NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'ReservedEmployeeId')
                ALTER TABLE WaitlistEntries ADD ReservedEmployeeId uniqueidentifier NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'BookingId')
                ALTER TABLE WaitlistEntries ADD BookingId uniqueidentifier NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('WaitlistEntries') AND name = 'BookedAt')
                ALTER TABLE WaitlistEntries ADD BookedAt datetime2 NULL;

            IF OBJECT_ID(N'AuditLogs', N'U') IS NULL
            BEGIN
                CREATE TABLE AuditLogs (
                    Id uniqueidentifier NOT NULL DEFAULT NEWID(),
                    TenantId uniqueidentifier NULL,
                    ActorType nvarchar(20) NOT NULL DEFAULT 'System',
                    ActorId uniqueidentifier NULL,
                    ActorName nvarchar(300) NULL,
                    Action nvarchar(100) NOT NULL DEFAULT '',
                    EntityType nvarchar(100) NULL,
                    EntityId nvarchar(100) NULL,
                    Details nvarchar(2000) NULL,
                    IpAddress nvarchar(64) NULL,
                    CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT PK_AuditLogs PRIMARY KEY (Id)
                );
                CREATE INDEX IX_AuditLogs_TenantId_CreatedAt ON AuditLogs(TenantId, CreatedAt);
                CREATE INDEX IX_AuditLogs_Action ON AuditLogs(Action);
            END
        ");
        Console.WriteLine("[SCHEMA-FALLBACK] OK");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SCHEMA-FALLBACK ERROR] {ex.Message}");
    }
}

try
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "logs", "checkpoint.txt"), $"{DateTime.UtcNow:O} PRE_RUN\n");
}
catch { /* best effort */ }

// Diagnostic safety net: if the app fails to start (e.g. an IHostedService throws during
// app.Run()'s host-startup sequence), write the real exception to a plain file — bypasses
// IIS/ANCM stdout log redirection entirely, which has proven unreliable for capturing
// early startup failures on this hosting environment.
try
{
    app.Run();
}
catch (Exception ex)
{
    try
    {
        var diagDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(diagDir);
        File.WriteAllText(Path.Combine(diagDir, "crash-diagnostic.txt"), $"{DateTime.UtcNow:O}\n{ex}");
    }
    catch { /* best effort */ }
    throw;
}

/// <summary>Hangfire dashboard: only allow requests from localhost.</summary>
public class LocalOnlyDashboardAuthorizationFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var remoteIp = context.Request.RemoteIpAddress;
        return !string.IsNullOrEmpty(remoteIp)
            && System.Net.IPAddress.TryParse(remoteIp, out var ip)
            && System.Net.IPAddress.IsLoopback(ip);
    }
}
