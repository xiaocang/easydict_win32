using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class ServiceQuerySelectionTests
{
    private static readonly ServiceOrigin Plugin = new(ServiceOriginKind.Plugin, "Bob");

    [Theory]
    [InlineData("bob:first:1")]
    [InlineData("google")]
    public void ServiceAction_QueriesOnlyItsTarget(string target)
    {
        var settings = new Dictionary<string, bool>
        {
            ["google"] = true,
            ["bob:first:1"] = false,
            ["bob:second:1"] = true
        };
        var rows = CreateRows();

        Apply(rows, settings, target);

        rows.Where(r => r.EnabledQuery).Select(r => r.ServiceId).Should().Equal(target);
        rows.Where(r => r.ServiceId != target).Should().OnlyContain(r => r.ShowPendingQueryHint && !r.IsExpanded);
        settings["bob:first:1"].Should().BeFalse("query selection must not alter saved settings");
        settings["google"].Should().BeTrue();
    }

    [Fact]
    public void OrdinaryQuery_AfterPluginAction_RestoresBuiltInsAndLeavesPluginsPending()
    {
        var rows = CreateRows();
        var settings = new Dictionary<string, bool> { ["bob:first:1"] = true };
        Apply(rows, settings, "bob:first:1");
        var plugin = rows[1];
        plugin.MarkQueried();
        plugin.Result = new TranslationResult
        {
            OriginalText = "text",
            TranslatedText = "previous plugin result",
            ServiceName = "Plugin"
        };

        Apply(rows, settings);

        rows.Where(r => r.EnabledQuery).Select(r => r.ServiceId).Should().Equal("google");
        rows.Where(r => r.IsPluginService).Should().OnlyContain(r => r.ShowPendingQueryHint && !r.IsExpanded);
        plugin.Result.Should().BeNull();
    }

    [Fact]
    public void MissingActionTarget_DoesNotFallBackToOtherServices()
    {
        var rows = CreateRows();

        Apply(rows, new Dictionary<string, bool>(), "bob:removed:1");

        rows.Should().OnlyContain(r => !r.EnabledQuery && r.ShowPendingQueryHint);
    }

    [Theory]
    [InlineData(ServiceOriginKind.BuiltIn, true)]
    [InlineData(ServiceOriginKind.ImportedDictionary, true)]
    [InlineData(ServiceOriginKind.Plugin, false)]
    public void OrdinaryQuery_UsesOriginForDefaults(ServiceOriginKind kind, bool expected)
    {
        ServiceQuerySelection.IsEnabled("service", new ServiceOrigin(kind), new Dictionary<string, bool>())
            .Should().Be(expected);
    }

    [Fact]
    public void OrdinaryQuery_PreservesManualBuiltInSetting()
    {
        ServiceQuerySelection.IsEnabled("google", ServiceOrigin.BuiltIn,
            new Dictionary<string, bool> { ["google"] = false }).Should().BeFalse();
    }

    private static List<ServiceQueryResult> CreateRows() =>
    [
        new() { ServiceId = "google" },
        new() { ServiceId = "bob:first:1", Origin = Plugin },
        new() { ServiceId = "bob:second:1", Origin = Plugin }
    ];

    private static void Apply(List<ServiceQueryResult> rows, IReadOnlyDictionary<string, bool> settings, string? target = null)
    {
        foreach (var row in rows)
        {
            row.EnabledQuery = ServiceQuerySelection.IsEnabled(row.ServiceId, row.Origin, settings, target);
            row.Reset();
        }
    }
}
