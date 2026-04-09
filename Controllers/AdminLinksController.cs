// Controllers/AdminLinksController.cs
// CRUD for tenant profile links (Linktree-style). TenantAdmin only.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/links")]
[Authorize]
public class AdminLinksController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;

    public AdminLinksController(GentleBookDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    /// <summary>Get all links for the current tenant, sorted by DisplayOrder.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var links = await _db.TenantLinks
            .OrderBy(l => l.DisplayOrder)
            .Select(l => new
            {
                l.Id, l.Title, l.Url, l.IconType, l.DisplayOrder, l.IsActive, l.CreatedAt
            })
            .ToListAsync();

        return Ok(links);
    }

    /// <summary>Create a new link.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLinkRequest request)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { message = "Titel und URL sind erforderlich." });

        var maxOrder = await _db.TenantLinks.AnyAsync()
            ? await _db.TenantLinks.MaxAsync(l => l.DisplayOrder)
            : -1;

        var link = new TenantLink
        {
            TenantId = tenantId,
            Title = request.Title.Trim(),
            Url = request.Url.Trim(),
            IconType = request.IconType,
            DisplayOrder = maxOrder + 1,
            IsActive = true,
        };

        _db.TenantLinks.Add(link);
        await _db.SaveChangesAsync();

        return Ok(new { link.Id, link.Title, link.Url, link.IconType, link.DisplayOrder, link.IsActive });
    }

    /// <summary>Update an existing link.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLinkRequest request)
    {
        var link = await _db.TenantLinks.FindAsync(id);
        if (link == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Title)) link.Title = request.Title.Trim();
        if (!string.IsNullOrWhiteSpace(request.Url)) link.Url = request.Url.Trim();
        if (request.IconType.HasValue) link.IconType = request.IconType.Value;
        if (request.IsActive.HasValue) link.IsActive = request.IsActive.Value;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { link.Id, link.Title, link.Url, link.IconType, link.DisplayOrder, link.IsActive });
    }

    /// <summary>Delete a link.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var link = await _db.TenantLinks.FindAsync(id);
        if (link == null) return NotFound();

        _db.TenantLinks.Remove(link);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Reorder links by passing an ordered array of IDs.</summary>
    [HttpPatch("reorder")]
    public async Task<IActionResult> Reorder([FromBody] Guid[] orderedIds)
    {
        var links = await _db.TenantLinks.ToListAsync();

        for (int i = 0; i < orderedIds.Length; i++)
        {
            var link = links.FirstOrDefault(l => l.Id == orderedIds[i]);
            if (link != null)
            {
                link.DisplayOrder = i;
                link.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateLinkRequest(string Title, string Url, LinkIconType IconType);
public record UpdateLinkRequest(string? Title, string? Url, LinkIconType? IconType, bool? IsActive);
