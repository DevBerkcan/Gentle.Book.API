namespace GentleBook.Api.DTOs;

// Original DTOs - Keep these for public API
public record ServiceDto(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    decimal Price,
    int DisplayOrder,
    string Currency
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
    List<EmployeeBasicDto>? AssignedEmployees = null  
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
    bool IsActive
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
    List<Guid>? EmployeeIds = null
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
    bool IsActive
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