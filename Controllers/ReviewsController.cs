// Controllers/ReviewsController.cs
// Public, anonymous endpoints reached via the review-request email link (see
// EmailService.SendReviewRequestEmailAsync / GenerateReviewToken). No auth — the HMAC-signed
// token is the only proof of identity, same trust model as the existing cancel/confirm links.
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Controllers;

[ApiController]
[Route("api/reviews")]
[AllowAnonymous]
public class ReviewsController : ControllerBase
{
    private readonly GentleBookDbContext _db;
    private readonly EmailService _emailService;
    private readonly ILogger<ReviewsController> _logger;

    public ReviewsController(GentleBookDbContext db, EmailService emailService, ILogger<ReviewsController> logger)
    {
        _db = db;
        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>Loads booking/service info for the review form (no rating submitted yet).</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Preview(string token)
    {
        Guid bookingId;
        try
        {
            var (id, action) = _emailService.DecodeToken(token);
            if (id == Guid.Empty || action != "review") return BadRequest(new { message = "Ungültiger Bewertungslink." });
            bookingId = id;
        }
        catch
        {
            return BadRequest(new { message = "Ungültiger Bewertungslink." });
        }

        var booking = await _db.Bookings.IgnoreQueryFilters()
            .Include(b => b.Service)
            .Include(b => b.Tenant)
            .FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return NotFound(new { message = "Buchung nicht gefunden." });

        var existing = await _db.Reviews.IgnoreQueryFilters()
            .AnyAsync(r => r.BookingId == bookingId);

        return Ok(new
        {
            alreadyReviewed = existing,
            serviceName = booking.Service.Name,
            tenantName = booking.Tenant.Name,
            bookingDate = booking.BookingDate,
        });
    }

    /// <summary>Submits the rating/comment. One review per booking — a second submit is rejected.</summary>
    [HttpPost("{token}")]
    public async Task<IActionResult> Submit(string token, [FromBody] SubmitReviewRequest request)
    {
        if (request.Rating < 1 || request.Rating > 5)
            return BadRequest(new { message = "Die Bewertung muss zwischen 1 und 5 Sternen liegen." });

        Guid bookingId;
        try
        {
            var (id, action) = _emailService.DecodeToken(token);
            if (id == Guid.Empty || action != "review") return BadRequest(new { message = "Ungültiger Bewertungslink." });
            bookingId = id;
        }
        catch
        {
            return BadRequest(new { message = "Ungültiger Bewertungslink." });
        }

        var booking = await _db.Bookings.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return NotFound(new { message = "Buchung nicht gefunden." });

        var alreadyReviewed = await _db.Reviews.IgnoreQueryFilters().AnyAsync(r => r.BookingId == bookingId);
        if (alreadyReviewed) return BadRequest(new { message = "Für diese Buchung wurde bereits eine Bewertung abgegeben." });

        var comment = request.Comment?.Trim();
        _db.Reviews.Add(new Review
        {
            TenantId = booking.TenantId,
            BookingId = booking.Id,
            CustomerId = booking.CustomerId,
            Rating = request.Rating,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment,
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation("Review submitted for booking {BookingId}: {Rating} stars", bookingId, request.Rating);
        return Ok(new { message = "Vielen Dank für Ihre Bewertung!" });
    }
}

public record SubmitReviewRequest(int Rating, string? Comment);
