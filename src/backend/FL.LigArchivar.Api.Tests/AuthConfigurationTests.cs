using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FL.LigArchivar.Api.Tests;

public class AuthConfigurationTests
{
    [Fact]
    public void AppSettings_DoNotIncludePassword()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        settings.RootElement.TryGetProperty("AUTH_PASSWORD", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t ")]
    public void Startup_WithoutValidPassword_Fails(string? password)
    {
        using var factory = new ArchiveApiFactory(ArchiveApiFactory.CreateDefaultMockFileSystem(),
            new Dictionary<string, string?> { ["AUTH_PASSWORD"] = password });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AUTH_PASSWORD*");
    }

    [Theory]
    [InlineData("test-password")]
    [InlineData("changeme")]
    public async Task Login_WithDefaultUsernameAndExplicitPassword_Authenticates(string password)
    {
        using var factory = new ArchiveApiFactory(ArchiveApiFactory.CreateDefaultMockFileSystem(),
            new Dictionary<string, string?> { ["AUTH_PASSWORD"] = password });
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "fl-admin", password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var archive = await client.GetAsync("/api/archive/tree");
        archive.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
