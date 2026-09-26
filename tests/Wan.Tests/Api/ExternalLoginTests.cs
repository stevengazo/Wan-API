using Wan.Api.Controllers;
using Wan.Domain.Entities;

namespace Wan.Tests.Api;

public class ExternalLoginTests
{
    [Theory]
    [InlineData(null, "ana@gmail.com", true)]
    [InlineData("miempresa.com", "ana@miempresa.com", true)]
    [InlineData("@miempresa.com, filial.com", "Luis@Filial.com", true)]
    [InlineData("miempresa.com", "ana@otraempresa.com", false)]
    [InlineData("miempresa.com", "ana@miempresa.com.evil.io", false)]
    public void RestrictsRegistrationByDomain(string? domains, string email, bool allowed) =>
        Assert.Equal(allowed, ExternalAuthController.DomainAllowed(new SsoSettings { AllowedDomains = domains }, email));
}
