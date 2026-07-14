using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;

namespace GentleBook.Api.Services;

/// <summary>
/// Best-effort audit logging: writes are never allowed to break the calling request.
/// </summary>
public class AuditService
{
    private readonly GentleBookDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(GentleBookDbContext db, IHttpContextAccessor httpContextAccessor, ILogger<AuditService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
        string action,
        string? entityType = null,
        string? entityId = null,
        string? details = null,
        Guid? tenantId = null)
    {
        try
        {
            var http = _httpContextAccessor.HttpContext;
            var user = http?.User;

            string actorType = "System";
            Guid? actorId = null;
            string? actorName = null;

            if (user?.Identity?.IsAuthenticated == true)
            {
                var role = JwtService.GetRole(user);
                actorType = role switch
                {
                    "SuperAdmin" => "SuperAdmin",
                    "TenantAdmin" => "TenantAdmin",
                    _ => "Employee",
                };
                actorId = JwtService.GetUserId(user);
                actorName = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
                    ?? JwtService.GetName(user)
                    ?? JwtService.GetUsername(user);

                var impersonatedBy = JwtService.GetImpersonatedBy(user);
                if (!string.IsNullOrEmpty(impersonatedBy))
                    actorName = $"{actorName} (impersonated by {impersonatedBy})";

                tenantId ??= JwtService.GetTenantId(user);
            }

            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                ActorType = actorType,
                ActorId = actorId,
                ActorName = actorName,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                IpAddress = http?.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Audit logging must never break the actual operation.
            _logger.LogError(ex, "Failed to write audit log entry for action {Action}", action);
        }
    }
}
