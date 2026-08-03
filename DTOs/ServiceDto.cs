namespace GentleBook.Api.DTOs;

// Original DTOs - Keep these for public API
public record ServiceDto(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    decimal Price,
    int DisplayOrder,
    string Currency,
    Guid? LocationId = null,
    string? LocationName = null
);

public record ServiceCategoryDto(
    Guid Id,
    string Name,
    string? Description,
    int DisplayOrder,
    bool IsActive,
    List<ServiceDto> Services
);

public record ServiceWithCategoryDto(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    decimal Price,
    int DisplayOrder,
    Guid CategoryId,
    string CategoryName,
    string Currency,
    List<EmployeeBasicDto>? AssignedEmployees = null,
    Guid? LocationId = null,
    string? LocationName = null
);

public record EmployeeBasicDto(
    Guid Id,
    string Name,
    string Role,
    string? Specialty
);

// For assigning service to employee
public record AssignServiceToEmployeeDto(
    Guid ServiceId,
    Guid EmployeeId
);

// NEW ADMIN DTOs
public record AdminServiceDto(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferTimeMinutes,
    decimal Price,
    int DisplayOrder,
    Guid CategoryId,
    string CategoryName,
    string Currency,
    List<EmployeeBasicDto> AssignedEmployees,
    bool IsActive,
    Guid? LocationId = null,
    string? LocationName = null
);

public record AdminServiceCategoryDto(
    Guid Id,
    string Name,
    string? Description,
    int DisplayOrder,
    bool IsActive,
    List<AdminServiceDto> Services
);

public record CreateServiceDto(
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferTimeMinutes,
    decimal Price,
    int DisplayOrder,
    Guid CategoryId,
    string Currency,
    List<Guid>? EmployeeIds = null,
    Guid? LocationId = null
);

public record UpdateServiceDto(
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferTimeMinutes,
    decimal Price,
    int DisplayOrder,
    Guid CategoryId,
    string Currency,
    List<Guid>? EmployeeIds,
    bool IsActive,
    Guid? LocationId = null
);

public record CreateCategoryDto(
    string Name,
    string? Description,
    int DisplayOrder
);

public record UpdateCategoryDto(
    string Name,
    string? Description,
    int DisplayOrder,
    bool IsActive
);

public record EmployeeForAssignmentDto(
    Guid Id,
    string Name,
    string Role,
    string? Specialty,
    int ServiceCount
);

public record BulkAssignDto(
    Guid EmployeeId,
    List<Guid> ServiceIds
);

public record PublicLocationDto(
    Guid Id,
    string Name,
    string? Street,
    string? PostalCode,
    string City
);
