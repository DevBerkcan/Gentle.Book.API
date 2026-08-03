// DTOs/EmployeeDto.cs
namespace GentleBook.Api.DTOs;

public record EmployeeListItemDto(
    Guid Id,
    string Name,
    string Role,
    string? Specialty,
    bool IsActive,
    string? Location
);

public record CreateEmployeeDto(
    string Name,
    string Role,
    string? Specialty,
    string? Location
);

public record UpdateEmployeeDto(
    string Name,
    string Role,
    string? Specialty,
    bool IsActive,
    string? Location
);

public record CreateEmployeeRequest(
    string Name,
    string? Role,
    string? Specialty,
    string? Username,
    string? Password,
    string? Location
);

public record UpdateEmployeeRequest(
    string? Name,
    string? Role,
    string? Specialty,
    string? Username,
    bool? IsActive,
    string? NewPassword,
    string? Location
);

public record EmployeeWithServicesDto(
    Guid Id,
    string Name,
    string Role,
    string? Specialty,
    bool IsActive,
    string? Location,
    List<ServiceBasicDto> AssignedServices,
    string? PhotoUrl = null,
    string? Tagline = null
);

public record ServiceBasicDto(
    Guid Id,
    string Name,
    int DurationMinutes,
    decimal Price
);

public record LoginRequest(string Username, string Password);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record SetPasswordRequest(string Username, string Password);