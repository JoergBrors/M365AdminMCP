using ApiServer.Services;

namespace ApiServer.Tests;

/// <summary>Regressionstests fuer die Parametervalidierung aus ADR-0008 (Path-Injection in den Graph-Pfad).</summary>
public class ReportParameterValidatorTests
{
    [Theory]
    [InlineData("2026-10-14")]
    [InlineData("2024-02-29")]
    public void Valid_dates_are_accepted(string date) => Assert.Equal(date, ReportParameterValidator.ValidateDate(date));

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("2023-02-29")]
    [InlineData("14.10.2026")]
    [InlineData("2026-10-14)/../../users")]
    [InlineData("2026-10-14,period='D7'")]
    [InlineData("")]
    [InlineData(" 2026-10-14")]
    public void Invalid_dates_are_rejected(string date) =>
        Assert.Throws<ArgumentException>(() => ReportParameterValidator.ValidateDate(date));

    [Fact]
    public void Valid_guid_is_accepted() =>
        Assert.Equal("75dcc81d-5cf4-406e-98c1-0a986b4c04ee",
            ReportParameterValidator.ValidateAppId("75DCC81D-5CF4-406E-98C1-0A986B4C04EE"));

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("75dcc81d-5cf4-406e-98c1-0a986b4c04ee'),x=('")]
    [InlineData("{75dcc81d-5cf4-406e-98c1-0a986b4c04ee}")]
    [InlineData("75dcc81d5cf4406e98c10a986b4c04ee")]
    public void Invalid_app_ids_are_rejected(string appId) =>
        Assert.Throws<ArgumentException>(() => ReportParameterValidator.ValidateAppId(appId));

    [Theory]
    [InlineData("Exchange Online")]
    [InlineData("microsoft.graph_v1-0")]
    public void Valid_service_areas_are_accepted(string area) =>
        Assert.Equal(area, ReportParameterValidator.ValidateServiceArea(area));

    [Theory]
    [InlineData("a'),b=('")]
    [InlineData("../users")]
    [InlineData("area/with/slash")]
    [InlineData("")]
    [InlineData("-leading-dash")]
    public void Invalid_service_areas_are_rejected(string area) =>
        Assert.Throws<ArgumentException>(() => ReportParameterValidator.ValidateServiceArea(area));
}
