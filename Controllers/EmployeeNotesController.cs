using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Authorize]
public class EmployeeNotesController : ControllerBase
{
    private readonly GentleBookDbContext _db;

    public EmployeeNotesController(GentleBookDbContext db)
    {
        _db = db;
    }

    // POST /api/employee-notes — employee sends a note to admin
    [HttpPost("api/employee-notes")]
    public async Task<IActionResult> SendNote([FromBody] SendNoteRequest request)
    {
        var employeeId = JwtService.GetEmployeeId(User);
        var tenantId   = JwtService.GetTenantId(User);

        if (employeeId == null || tenantId == null)
            return Unauthorized();

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId);

        if (employee == null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Subject and message are required." });

        var note = new EmployeeNote
        {
            TenantId     = tenantId.Value,
            EmployeeId   = employeeId.Value,
            EmployeeName = employee.Name,
            Subject      = request.Subject.Trim(),
            Message      = request.Message.Trim(),
        };

        _db.EmployeeNotes.Add(note);
        await _db.SaveChangesAsync();

        return Ok(new { success = true, data = MapNote(note) });
    }

    // GET /api/employee-notes — employee sees own sent notes
    [HttpGet("api/employee-notes")]
    public async Task<IActionResult> GetMyNotes()
    {
        var employeeId = JwtService.GetEmployeeId(User);
        var tenantId   = JwtService.GetTenantId(User);

        if (employeeId == null || tenantId == null)
            return Unauthorized();

        var notes = await _db.EmployeeNotes
            .Where(n => n.TenantId == tenantId && n.EmployeeId == employeeId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        return Ok(new { success = true, data = notes.Select(MapNote) });
    }

    // GET /api/admin/employee-notes — admin sees all tenant notes
    [HttpGet("api/admin/employee-notes")]
    public async Task<IActionResult> GetAllNotes()
    {
        var role     = JwtService.GetRole(User);
        var tenantId = JwtService.GetTenantId(User);

        if (role != "TenantAdmin" || tenantId == null)
            return Forbid();

        var notes = await _db.EmployeeNotes
            .Where(n => n.TenantId == tenantId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        return Ok(new { success = true, data = notes.Select(MapNote) });
    }

    // PUT /api/admin/employee-notes/{id}/read — admin marks note as read
    [HttpPut("api/admin/employee-notes/{id}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var role     = JwtService.GetRole(User);
        var tenantId = JwtService.GetTenantId(User);

        if (role != "TenantAdmin" || tenantId == null)
            return Forbid();

        var note = await _db.EmployeeNotes
            .FirstOrDefaultAsync(n => n.Id == id && n.TenantId == tenantId);

        if (note == null) return NotFound();

        note.IsRead = true;
        await _db.SaveChangesAsync();

        return Ok(new { success = true });
    }

    private static object MapNote(EmployeeNote n) => new
    {
        n.Id,
        n.EmployeeId,
        n.EmployeeName,
        n.Subject,
        n.Message,
        n.IsRead,
        n.CreatedAt,
    };
}

public record SendNoteRequest(string Subject, string Message);
