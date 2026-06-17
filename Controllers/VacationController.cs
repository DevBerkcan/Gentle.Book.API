using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;  // ITenantContext lives here
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/admin/vacations")]
[Authorize]
public class VacationController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly ITenantContext _tenantContext;

    public VacationController(GentleBookDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    // GET /api/admin/vacations?employeeId=&year=&month=
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] int? year = null,
        [FromQuery] int? month = null)
    {
        var query = _db.EmployeeVacations
            .Include(v => v.Employee)
            .AsQueryable();

        if (employeeId.HasValue)
            query = query.Where(v => v.EmployeeId == employeeId.Value);

        if (year.HasValue && month.HasValue)
        {
            var start = new DateOnly(year.Value, month.Value, 1);
            var end = start.AddMonths(1).AddDays(-1);
            query = query.Where(v => v.StartDate <= end && v.EndDate >= start);
        }
        else if (year.HasValue)
        {
            var start = new DateOnly(year.Value, 1, 1);
            var end = new DateOnly(year.Value, 12, 31);
            query = query.Where(v => v.StartDate <= end && v.EndDate >= start);
        }

        var vacations = await query
            .OrderBy(v => v.StartDate)
            .Select(v => new
            {
                v.Id,
                v.EmployeeId,
                EmployeeName = v.Employee.Name,
                StartDate = v.StartDate.ToString("yyyy-MM-dd"),
                EndDate = v.EndDate.ToString("yyyy-MM-dd"),
                v.Type,
                v.Note,
                v.CreatedAt,
            })
            .ToListAsync();

        return Ok(vacations);
    }

    // POST /api/admin/vacations
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVacationDto dto)
    {
        var tenantId = _tenantContext.TenantId;
        if (!tenantId.HasValue) return Unauthorized();

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == dto.EmployeeId);
        if (employee == null)
            return NotFound(new { message = "Mitarbeiter nicht gefunden" });

        if (DateOnly.Parse(dto.StartDate) > DateOnly.Parse(dto.EndDate))
            return BadRequest(new { message = "Startdatum muss vor Enddatum liegen" });

        var vacation = new EmployeeVacation
        {
            TenantId = tenantId.Value,
            EmployeeId = dto.EmployeeId,
            StartDate = DateOnly.Parse(dto.StartDate),
            EndDate = DateOnly.Parse(dto.EndDate),
            Type = dto.Type ?? "Vacation",
            Note = dto.Note,
        };

        _db.EmployeeVacations.Add(vacation);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { employeeId = vacation.EmployeeId }, new
        {
            vacation.Id,
            vacation.EmployeeId,
            EmployeeName = employee.Name,
            StartDate = vacation.StartDate.ToString("yyyy-MM-dd"),
            EndDate = vacation.EndDate.ToString("yyyy-MM-dd"),
            vacation.Type,
            vacation.Note,
        });
    }

    // PUT /api/admin/vacations/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateVacationDto dto)
    {
        var vacation = await _db.EmployeeVacations.FirstOrDefaultAsync(v => v.Id == id);
        if (vacation == null) return NotFound();

        if (DateOnly.Parse(dto.StartDate) > DateOnly.Parse(dto.EndDate))
            return BadRequest(new { message = "Startdatum muss vor Enddatum liegen" });

        vacation.StartDate = DateOnly.Parse(dto.StartDate);
        vacation.EndDate = DateOnly.Parse(dto.EndDate);
        vacation.Type = dto.Type ?? vacation.Type;
        vacation.Note = dto.Note;

        await _db.SaveChangesAsync();
        return Ok(new
        {
            vacation.Id,
            vacation.EmployeeId,
            StartDate = vacation.StartDate.ToString("yyyy-MM-dd"),
            EndDate = vacation.EndDate.ToString("yyyy-MM-dd"),
            vacation.Type,
            vacation.Note,
        });
    }

    // DELETE /api/admin/vacations/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var vacation = await _db.EmployeeVacations.FirstOrDefaultAsync(v => v.Id == id);
        if (vacation == null) return NotFound();

        _db.EmployeeVacations.Remove(vacation);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateVacationDto(
    Guid EmployeeId,
    string StartDate,
    string EndDate,
    string? Type,
    string? Note
);
