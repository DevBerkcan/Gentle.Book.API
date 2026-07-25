using GentleBook.Api.DTOs;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AvailabilityController : ControllerBase
{
    private readonly AvailabilityService _availabilityService;
    private readonly ILogger<AvailabilityController> _logger;

    public AvailabilityController(
        AvailabilityService availabilityService,
        ILogger<AvailabilityController> logger)
    {
        _availabilityService = availabilityService;
        _logger = logger;
    }

    /// <summary>
    /// Get available time slots for a service on a specific date.
    /// Optionally filter by employee to see availability for a specific employee.
    /// </summary>
    [HttpGet("{serviceId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AvailabilityResponseDto>> GetAvailability(
        Guid serviceId,
        [FromQuery] string date,
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? waitlistToken = null)
    {
        if (!DateOnly.TryParse(date, out var bookingDate))
            return BadRequest(new { message = "Ungültiges Datumsformat" });

        try
        {
            var availability = await _availabilityService.GetAvailableTimeSlotsAsync(
                serviceId, bookingDate, employeeId, waitlistToken);
            return Ok(availability);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get availability for all employees on a specific date
    /// </summary>
    [HttpGet("all-employees")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Dictionary<Guid, List<TimeSlotDto>>>> GetAllEmployeesAvailability(
        [FromQuery] string date,
        [FromQuery] int serviceDuration)
    {
        if (!DateOnly.TryParse(date, out var bookingDate))
            return BadRequest(new { message = "Ungültiges Datumsformat" });

        var availability = await _availabilityService.GetAllEmployeesAvailabilityAsync(
            bookingDate, serviceDuration);
        return Ok(availability);
    }

    /// <summary>
    /// Check if a specific time slot is available for an employee
    /// </summary>
    [HttpGet("check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<bool>> CheckSlotAvailability(
        [FromQuery] string date,
        [FromQuery] string startTime,
        [FromQuery] string endTime,
        [FromQuery] Guid employeeId)
    {
        if (!DateOnly.TryParse(date, out var bookingDate))
            return BadRequest(new { message = "Ungültiges Datumsformat" });

        if (!TimeOnly.TryParse(startTime, out var start))
            return BadRequest(new { message = "Ungültiges Startzeit-Format" });

        if (!TimeOnly.TryParse(endTime, out var end))
            return BadRequest(new { message = "Ungültiges Endzeit-Format" });

        var isAvailable = await _availabilityService.IsTimeSlotAvailableForEmployeeAsync(
            bookingDate, start, end, employeeId);

        return Ok(isAvailable);
    }

    /// <summary>
    /// Get all available employees for a given time slot
    /// </summary>
    [HttpGet("available-employees")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<Guid>>> GetAvailableEmployees(
        [FromQuery] string date,
        [FromQuery] string startTime,
        [FromQuery] string endTime)
    {
        if (!DateOnly.TryParse(date, out var bookingDate))
            return BadRequest(new { message = "Ungültiges Datumsformat" });

        if (!TimeOnly.TryParse(startTime, out var start))
            return BadRequest(new { message = "Ungültiges Startzeit-Format" });

        if (!TimeOnly.TryParse(endTime, out var end))
            return BadRequest(new { message = "Ungültiges Endzeit-Format" });

        var availableEmployees = await _availabilityService.GetAvailableEmployeesForTimeSlotAsync(
            bookingDate, start, end);

        return Ok(availableEmployees);
    }
}
