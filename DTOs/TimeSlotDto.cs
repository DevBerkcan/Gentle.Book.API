namespace GentleBook.Api.DTOs;

public record TimeSlotDto(
    string StartTime,
    string EndTime,
    bool IsAvailable
);

public record AvailabilityResponseDto(
    string Date,
    Guid ServiceId,
    int ServiceDuration,
    List<TimeSlotDto> AvailableSlots
);
