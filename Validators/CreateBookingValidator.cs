using GentleBook.Api.DTOs;
using FluentValidation;

namespace GentleBook.Api.Validators;

public class CreateBookingValidator : AbstractValidator<CreateBookingDto>
{
    public CreateBookingValidator()
    {
        RuleFor(x => x.ServiceId)
            .NotEmpty().WithMessage("Service ID ist erforderlich");

        RuleFor(x => x.BookingDate)
            .NotEmpty().WithMessage("Buchungsdatum ist erforderlich")
            .Must(BeValidDate).WithMessage("Ungültiges Datumsformat. Verwende YYYY-MM-DD");

        RuleFor(x => x.StartTime)
            .NotEmpty().WithMessage("Startzeit ist erforderlich")
            .Matches(@"^([0-1][0-9]|2[0-3]):[0-5][0-9]$").WithMessage("Ungültiges Zeitformat. Verwende HH:mm");

        RuleFor(x => x.Customer)
            .NotNull().WithMessage("Kundendaten sind erforderlich")
            .SetValidator(new CustomerInfoValidator());
    }

    private bool BeValidDate(string dateStr)
    {
        return DateOnly.TryParse(dateStr, out _);
    }
}

public class CustomerInfoValidator : AbstractValidator<CustomerInfoDto>
{
    public CustomerInfoValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("Vorname ist erforderlich")
            .MaximumLength(100).WithMessage("Vorname darf maximal 100 Zeichen haben");

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Nachname ist erforderlich")
            .MaximumLength(100).WithMessage("Nachname darf maximal 100 Zeichen haben");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email ist erforderlich")
            .EmailAddress().WithMessage("Ungültige Email-Adresse")
            .MaximumLength(255).WithMessage("Email darf maximal 255 Zeichen haben");

        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Telefonnummer ist erforderlich")
            .Matches(@"^[\d\s\+\-\(\)]+$").WithMessage("Ungültige Telefonnummer")
            .MaximumLength(20).WithMessage("Telefonnummer darf maximal 20 Zeichen haben");
    }
}
