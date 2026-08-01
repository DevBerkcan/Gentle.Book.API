// DTOs/BrandImportDtos.cs
// Request bodies for the AI Brand Import feature (api/admin/brand-import/*).
namespace GentleBook.Api.DTOs;

public record UpdateBrandImportSettingsRequest(string? WebsiteUrl, bool? ConsentConfirmed);

public record StartBrandAnalysisRequest(string WebsiteUrl, bool ConfirmConsent);

public record ApplyBrandProposalRequest(
    Guid ProposalId,
    bool ApplyLogo,
    bool ApplyColors,
    bool ApplyTypography,
    bool ApplyDescription,
    bool ApplySocialLinks,
    Guid? SelectedLogoAssetId);
