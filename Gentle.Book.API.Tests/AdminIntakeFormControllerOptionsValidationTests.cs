using Gentle.Book.API.Tests.TestSupport;
using GentleBook.Api.Controllers;
using GentleBook.Api.Data;
using GentleBook.Api.Data.Entities;
using GentleBook.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gentle.Book.API.Tests;

// Found during live pre-launch testing: a MultipleChoice ("Einfachauswahl") field saved
// successfully with zero options — nothing validated OptionsJson for choice-type fields.
public class AdminIntakeFormControllerOptionsValidationTests
{
    private static (Tenant tenant, AdminIntakeFormController controller) Build(GentleBookDbContext db)
    {
        var (tenant, _) = AgencyTenantFactory.Seed(db, SubscriptionPlan.Agency, IndustryType.Hairdresser);
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant.Id, "test-tenant", role: "TenantAdmin");
        var controller = new AdminIntakeFormController(db, tenantContext, NullLogger<AdminIntakeFormController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = ClaimsPrincipalFactory.TenantAdmin(Guid.NewGuid(), tenant.Id) },
            },
        };
        return (tenant, controller);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"Nur eine Option\"]")]
    public async Task CreateField_MultipleChoiceWithoutEnoughOptions_Rejected(string? optionsJson)
    {
        using var db = TestDbContextFactory.Create();
        var (_, controller) = Build(db);

        var result = await controller.CreateField(new IntakeFormFieldRequest(
            "Hauttyp", "MultipleChoice", "Anamnese", optionsJson, null, null, null, false, null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateField_MultipleChoiceWithTwoOptions_Succeeds()
    {
        using var db = TestDbContextFactory.Create();
        var (_, controller) = Build(db);

        var result = await controller.CreateField(new IntakeFormFieldRequest(
            "Hauttyp", "MultipleChoice", "Anamnese", "[\"Trocken\",\"Fettig\"]", null, null, null, false, null));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task CreateField_TextType_DoesNotRequireOptions()
    {
        using var db = TestDbContextFactory.Create();
        var (_, controller) = Build(db);

        var result = await controller.CreateField(new IntakeFormFieldRequest(
            "Anmerkungen", "Text", "Anamnese", null, null, null, null, false, null));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateField_ChangingToMultipleChoiceWithoutOptions_Rejected()
    {
        using var db = TestDbContextFactory.Create();
        var (tenant, controller) = Build(db);
        var field = new IntakeFormField { TenantId = tenant.Id, Label = "Hauttyp", FieldType = IntakeFormFieldType.Text, FormType = IntakeFormType.Anamnese };
        db.IntakeFormFields.Add(field);
        db.SaveChanges();

        var result = await controller.UpdateField(field.Id, new IntakeFormFieldRequest(
            "Hauttyp", "MultipleChoice", "Anamnese", null, null, null, null, false, null));

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
