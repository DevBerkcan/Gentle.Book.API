using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GentleBook.Api.Services.AI;

public interface IBookingDraftService
{
    Task<ServiceFinderBookingDraft> CreateDraftAsync(Guid tenantId, CreateBookingDraftRequestDto request, CancellationToken cancellationToken);
    Task<BookingResponseDto> ConfirmDraftAsync(Guid tenantId, Guid draftId, bool customerConfirmed, CancellationToken cancellationToken);
    Task ExpireOutdatedDraftsAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class BookingDraftService : IBookingDraftService
{
    private static readonly TimeSpan DraftTtl = TimeSpan.FromMinutes(12);

    private readonly GentleBookDbContext _db;
    private readonly BookingService _bookingService;

    public BookingDraftService(GentleBookDbContext db, BookingService bookingService)
    {
        _db = db;
        _bookingService = bookingService;
    }

    public async Task<ServiceFinderBookingDraft> CreateDraftAsync(
        Guid tenantId,
        CreateBookingDraftRequestDto request,
        CancellationToken cancellationToken)
    {
        var service = await _db.Services
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.ServiceId && s.TenantId == tenantId && s.IsActive, cancellationToken);

        if (service == null)
            throw new ArgumentException("Service not found or inactive.", nameof(request.ServiceId));

        if (!DateOnly.TryParse(request.BookingDate, out var bookingDate))
            throw new ArgumentException("Invalid booking date.", nameof(request.BookingDate));

        if (!TimeOnly.TryParse(request.StartTime, out var startTime))
            throw new ArgumentException("Invalid start time.", nameof(request.StartTime));

        if (bookingDate.ToDateTime(startTime) < DateTime.UtcNow)
            throw new InvalidOperationException("A booking draft cannot be created in the past.");

        if (request.EmployeeId.HasValue)
        {
            var employeeHasService = await _db.ServiceEmployees
                .AsNoTracking()
                .AnyAsync(se => se.ServiceId == request.ServiceId && se.EmployeeId == request.EmployeeId.Value, cancellationToken);
            if (!employeeHasService)
                throw new InvalidOperationException("Selected employee is not assigned to this service.");
        }

        var draft = new ServiceFinderBookingDraft
        {
            TenantId = tenantId,
            ServiceId = request.ServiceId,
            EmployeeId = request.EmployeeId,
            BookingDate = bookingDate,
            StartTime = startTime,
            FirstName = request.Customer.FirstName.Trim(),
            LastName = request.Customer.LastName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Customer.Email) ? null : request.Customer.Email.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Customer.Phone) ? null : request.Customer.Phone.Trim(),
            CustomerNotes = string.IsNullOrWhiteSpace(request.CustomerNotes) ? null : request.CustomerNotes.Trim(),
            ExpiresAt = DateTime.UtcNow.Add(DraftTtl),
            Status = BookingDraftStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _db.ServiceFinderBookingDrafts.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);

        return draft;
    }

    public async Task<BookingResponseDto> ConfirmDraftAsync(
        Guid tenantId,
        Guid draftId,
        bool customerConfirmed,
        CancellationToken cancellationToken)
    {
        if (!customerConfirmed)
            throw new InvalidOperationException("Customer confirmation is required.");

        var draft = await _db.ServiceFinderBookingDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.TenantId == tenantId, cancellationToken);

        if (draft == null)
            throw new ArgumentException("Draft not found.", nameof(draftId));

        if (draft.Status != BookingDraftStatus.Pending)
            throw new InvalidOperationException("Draft cannot be confirmed in its current state.");

        if (draft.ExpiresAt <= DateTime.UtcNow)
        {
            draft.Status = BookingDraftStatus.Expired;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Draft expired. Please pick a slot again.");
        }

        var booking = await _bookingService.CreateBookingAsync(new CreateBookingDto(
            draft.ServiceId,
            draft.BookingDate.ToString("yyyy-MM-dd"),
            draft.StartTime.ToString("HH:mm"),
            new CustomerInfoDto(draft.FirstName, draft.LastName, draft.Email, draft.Phone),
            draft.CustomerNotes,
            draft.EmployeeId,
            WaitlistToken: null));

        draft.Status = BookingDraftStatus.Confirmed;
        draft.ConfirmedAt = DateTime.UtcNow;
        draft.ConfirmedBookingId = booking.Id;
        await _db.SaveChangesAsync(cancellationToken);

        return booking;
    }

    public async Task ExpireOutdatedDraftsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await _db.ServiceFinderBookingDrafts
            .Where(d => d.TenantId == tenantId && d.Status == BookingDraftStatus.Pending && d.ExpiresAt <= DateTime.UtcNow)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, BookingDraftStatus.Expired), cancellationToken);
    }
}
