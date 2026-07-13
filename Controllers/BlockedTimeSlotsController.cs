using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BlockedTimeSlotsController : ControllerBase
{
    private readonly BlockedTimeSlotService _blockedTimeSlotService;
    private readonly ILogger<BlockedTimeSlotsController> _logger;

    public BlockedTimeSlotsController(
        BlockedTimeSlotService blockedTimeSlotService,
        ILogger<BlockedTimeSlotsController> logger)
    {
        _blockedTimeSlotService = blockedTimeSlotService;
        _logger = logger;
    }

    private Guid? GetCurrentEmployeeId()
    {
        var role = JwtService.GetRole(User);
        return role is "TenantAdmin" or "SuperAdmin" ? null : JwtService.GetEmployeeId(User);
    }

    private bool IsAdminRequest() => JwtService.GetRole(User) is "TenantAdmin" or "SuperAdmin";

    [HttpGet]
    public async Task<ActionResult<List<BlockedTimeSlotDto>>> GetBlockedTimeSlots(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] bool all = false)
    {
        var employeeId = (all && IsAdminRequest()) ? null : GetCurrentEmployeeId();

        var blockedSlots = await _blockedTimeSlotService.GetBlockedTimeSlotsAsync(
            DateOnly.FromDateTime(startDate ?? DateTime.MinValue),
            DateOnly.FromDateTime(endDate ?? DateTime.MaxValue),
            employeeId);

        return Ok(blockedSlots);
    }

    [HttpGet("by-date")]
    [AllowAnonymous]
    public async Task<ActionResult<List<BlockedTimeSlotDto>>> GetByDate(
        [FromQuery] DateTime date,
        [FromQuery] Guid? employeeId)
    {
        var dateOnly = DateOnly.FromDateTime(date);
        var slots = await _blockedTimeSlotService.GetBlockedTimeSlotsAsync(
            dateOnly, dateOnly, employeeId);
        return Ok(slots);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<BlockedTimeSlotDto>> GetBlockedTimeSlot(Guid id)
    {
        var blockedSlot = await _blockedTimeSlotService.GetBlockedTimeSlotByIdAsync(id);
        if (blockedSlot == null)
            return NotFound(new { message = "Blocked time slot not found" });

        if (!IsAdminRequest())
        {
            var empId = GetCurrentEmployeeId();
            if (empId != null && blockedSlot.EmployeeId != empId)
                return Forbid();
        }

        return Ok(blockedSlot);
    }

    [HttpPost]
    public async Task<ActionResult<BlockedTimeSlotDto>> CreateBlockedTimeSlot(
        [FromBody] CreateBlockedTimeSlotDto dto)
    {
        var scopedDto = dto with { EmployeeId = GetCurrentEmployeeId() };

        try
        {
            var blockedSlot = await _blockedTimeSlotService.CreateBlockedTimeSlotAsync(scopedDto);
            _logger.LogInformation("Blocked time slot created: {Id}", blockedSlot.Id);

            return CreatedAtAction(
                nameof(GetBlockedTimeSlot),
                new { id = blockedSlot.Id },
                blockedSlot);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("range")]
    public async Task<ActionResult<List<BlockedTimeSlotDto>>> CreateBlockedDateRange(
        [FromBody] CreateBlockedDateRangeDto dto)
    {
        var scopedDto = dto with { EmployeeId = GetCurrentEmployeeId() };

        try
        {
            var blockedSlots = await _blockedTimeSlotService.CreateBlockedDateRangeAsync(scopedDto);
            _logger.LogInformation("Blocked time slots created in range: {Count} slots", blockedSlots.Count);
            return Ok(blockedSlots);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<BlockedTimeSlotDto>> UpdateBlockedTimeSlot(
        Guid id,
        [FromBody] UpdateBlockedTimeSlotDto dto)
    {
        if (!IsAdminRequest())
        {
            var existing = await _blockedTimeSlotService.GetBlockedTimeSlotByIdAsync(id);
            if (existing == null)
                return NotFound(new { message = "Blocked time slot not found" });

            var empId = GetCurrentEmployeeId();
            if (empId != null && existing.EmployeeId != empId)
                return Forbid();
        }

        try
        {
            var blockedSlot = await _blockedTimeSlotService.UpdateBlockedTimeSlotAsync(id, dto);
            return Ok(blockedSlot);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteBlockedTimeSlot(Guid id)
    {
        if (!IsAdminRequest())
        {
            var existing = await _blockedTimeSlotService.GetBlockedTimeSlotByIdAsync(id);
            if (existing == null)
                return NotFound(new { message = "Blocked time slot not found" });

            var empId = GetCurrentEmployeeId();
            if (empId != null && existing.EmployeeId != empId)
                return Forbid();
        }

        try
        {
            await _blockedTimeSlotService.DeleteBlockedTimeSlotAsync(id);
            _logger.LogInformation("Blocked time slot deleted: {Id}", id);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
