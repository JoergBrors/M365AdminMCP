using ApiServer.Health;
using McpServer.Utils;
using Microsoft.Extensions.Configuration;

namespace ApiServer.Tests.Health;

public class HealthInputValidatorTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Subscription_ids_are_normalized_to_lowercase_guid()
    {
        Assert.True(HealthInputValidator.TryParseSubscriptionId("  AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE ", out var id, out _));
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Invalid_subscription_ids_are_rejected(string? value) =>
        Assert.False(HealthInputValidator.TryParseSubscriptionId(value, out _, out _));

    [Fact]
    public void Valid_resource_ids_are_accepted_including_nested_types()
    {
        Assert.True(HealthInputValidator.TryParseResourceId(
            $"/subscriptions/{T.SubA}/resourceGroups/rg-1/providers/Microsoft.Sql/servers/srv1/databases/db_1", out var id, out var sub, out _));
        Assert.Equal(T.SubA, sub);
        Assert.EndsWith("databases/db_1", id);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z", 2026, 10, 1, 0)]
    [InlineData("2026-10-01T02:00:00+02:00", 2026, 10, 1, 0)]
    [InlineData("2026-10-01", 2026, 10, 1, 0)]
    public void SinceUtc_is_parsed_to_utc(string value, int y, int m, int d, int h)
    {
        Assert.True(HealthInputValidator.TryParseSinceUtc(value, Now, out var since, out _));
        Assert.Equal(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc), since!.Value);
    }

    [Theory]
    [InlineData("gestern")]
    [InlineData("2099-01-01T00:00:00Z")]
    public void Invalid_or_future_sinceUtc_is_rejected(string value) =>
        Assert.False(HealthInputValidator.TryParseSinceUtc(value, Now, out _, out _));

    [Fact]
    public void Empty_sinceUtc_means_no_filter() =>
        Assert.True(HealthInputValidator.TryParseSinceUtc(null, Now, out var since, out _) && since is null);

    [Theory]
    [InlineData(null, true, 100)]
    [InlineData(1, true, 1)]
    [InlineData(1000, true, 1000)]
    [InlineData(0, false, 0)]
    [InlineData(1001, false, 1001)]
    [InlineData(-5, false, -5)]
    public void MaxResults_is_bounded(int? value, bool ok, int expected)
    {
        Assert.Equal(ok, HealthInputValidator.TryNormalizeMaxResults(value, 100, 1000, out var max, out _));
        Assert.Equal(expected, max);
    }

    // ---------- Options-Validierung beim Start ----------

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(MicrosoftHealthOptions options, string? appTenant = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(appTenant is null ? [] : new Dictionary<string, string?> { ["AzureAd:TenantId"] = appTenant })
            .Build();
        return new MicrosoftHealthOptionsValidator(configuration).Validate(null, options);
    }

    [Fact]
    public void Default_options_are_valid() => Assert.True(Validate(new MicrosoftHealthOptions()).Succeeded);

    [Fact]
    public void Invalid_subscription_in_options_fails_startup_validation_without_echoing_value()
    {
        var result = Validate(new MicrosoftHealthOptions { AllowedSubscriptionIds = ["kein-guid-GEHEIM"] });

        Assert.True(result.Failed);
        Assert.DoesNotContain("GEHEIM", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Tenant_mismatch_fails_validation_to_prevent_cross_tenant_queries()
    {
        Assert.True(Validate(new MicrosoftHealthOptions { TenantId = "tenant-a" }, "tenant-b").Failed);
        Assert.True(Validate(new MicrosoftHealthOptions { TenantId = "TENANT-A" }, "tenant-a").Succeeded);
    }

    [Theory]
    [InlineData(0, 100, 20)]
    [InlineData(30, 0, 20)]
    [InlineData(30, 100, 0)]
    [InlineData(301, 100, 20)]
    public void Out_of_range_limits_fail_validation(int timeout, int maxResults, int maxPages) =>
        Assert.True(Validate(new MicrosoftHealthOptions { RequestTimeoutSeconds = timeout, MaxResults = maxResults, MaxPages = maxPages }).Failed);
}
