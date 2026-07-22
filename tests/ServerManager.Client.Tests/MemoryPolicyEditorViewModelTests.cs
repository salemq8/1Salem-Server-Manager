using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client.Tests;

public sealed class MemoryPolicyEditorViewModelTests
{
    private const long GiB = ResourcePolicyCatalog.Gibibyte;

    [Fact]
    public void CustomSelection_ChangesAuthoritativePresetAndRaisesDependencies()
    {
        var editor = CreateEditor();
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        editor.SelectPresetCommand.Execute("Custom");

        Assert.Equal(ResourceMode.Custom, editor.SelectedMemoryPolicyPreset);
        Assert.True(editor.IsCustomPolicy);
        Assert.True(editor.CanEditMemoryThresholds);
        Assert.False(editor.MemoryFieldsReadOnly);
        Assert.Contains(nameof(editor.SelectedMemoryPolicyPreset), changed);
        Assert.Contains(nameof(editor.IsCustomPolicy), changed);
        Assert.Contains(nameof(editor.CanEditMemoryThresholds), changed);
        Assert.Contains(nameof(editor.ActiveProfileSummary), changed);
    }

    [Fact]
    public void BalancedPreset_KeepsMemoryFieldsReadOnly()
    {
        var editor = CreateEditor();

        Assert.Equal(ResourceMode.Balanced, editor.SelectedMemoryPolicyPreset);
        Assert.False(editor.IsCustomPolicy);
        Assert.False(editor.CanEditMemoryThresholds);
        Assert.True(editor.MemoryFieldsReadOnly);
    }

    [Fact]
    public void MetricsRefresh_DoesNotOverwritePendingCustomText()
    {
        var editor = CreateEditor();
        editor.SelectPresetCommand.Execute("Custom");
        editor.TotalServerBudgetGiB = "7.50";

        editor.LoadSnapshot(CreateSnapshot(
            budget: 9 * GiB,
            available: 10 * GiB));

        Assert.Equal("7.50", editor.TotalServerBudgetGiB);
        Assert.True(editor.HasUnsavedChanges);
    }

    [Fact]
    public void DecimalInput_UsesCurrentLocaleAndInvariantFallback()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(
                MemoryPolicyEditorViewModel.TryParsePositiveGiB(
                    "7,50",
                    out var localized));
            Assert.True(
                MemoryPolicyEditorViewModel.TryParsePositiveGiB(
                    "7.50",
                    out var invariant));
            Assert.Equal((long)(7.5 * GiB), localized);
            Assert.Equal(localized, invariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Validation_RetainsInvalidTextAndReportsExactErrors()
    {
        var editor = CreateEditor();
        editor.SelectPresetCommand.Execute("Custom");
        editor.WarningThresholdGiB = "8.00";
        editor.CriticalThresholdGiB = "7.00";
        editor.TotalServerBudgetGiB = "20.00";

        Assert.True(editor.HasErrors);
        Assert.Equal("20.00", editor.TotalServerBudgetGiB);
        Assert.Contains(
            editor.GetErrors(nameof(editor.CriticalThresholdGiB)).Cast<string>(),
            error => error.Contains(
                "greater than or equal",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            editor.GetErrors(nameof(editor.TotalServerBudgetGiB)).Cast<string>(),
            error => error.Contains(
                "cannot exceed",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cancel_RestoresLastSavedValues()
    {
        var editor = CreateEditor();
        editor.SelectPresetCommand.Execute("Custom");
        editor.WindowsReserveGiB = "6.00";
        Assert.True(editor.HasUnsavedChanges);

        editor.Cancel();

        Assert.Equal(ResourceMode.Balanced, editor.SelectedMemoryPolicyPreset);
        Assert.Equal("3.00", editor.WindowsReserveGiB);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public void SaveRequest_ContainsValidatedCustomValuesAndOverridePreference()
    {
        var editor = CreateEditor();
        editor.SelectPresetCommand.Execute("Custom");
        editor.WindowsReserveGiB = "6.00";
        editor.WarningThresholdGiB = "5.00";
        editor.CriticalThresholdGiB = "7.00";
        editor.TotalServerBudgetGiB = "7.50";
        editor.AllowUnsafeStartupOverride = true;

        var valid = editor.TryBuildRequest(out var request, out var error);

        Assert.True(valid, error);
        Assert.Equal(ResourceMode.Custom, request.Mode);
        Assert.Equal(6 * GiB, request.WindowsReserveBytes);
        Assert.Equal(
            (long?)(7.5 * GiB),
            request.MaximumServerBudgetBytes);
        Assert.True(request.AllowUnsafeStartupOverride);
    }

    [Fact]
    public void Summary_OmitsUnregisteredMinecraft()
    {
        var editor = CreateEditor(hasMinecraft: false);

        Assert.Equal(
            "Active: Balanced · Palworld Normal",
            editor.ActiveProfileSummary);
        Assert.DoesNotContain(
            "Minecraft",
            editor.ActiveProfileSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyRecommended_ProducesValidCustomValues()
    {
        var editor = CreateEditor();
        editor.SelectPresetCommand.Execute("Custom");

        editor.ApplyRecommendedCommand.Execute(null);

        Assert.True(
            editor.TryBuildRequest(out _, out var error),
            error);
        Assert.True(editor.HasUnsavedChanges);
    }

    private static MemoryPolicyEditorViewModel CreateEditor(
        bool hasMinecraft = false)
    {
        var editor = new MemoryPolicyEditorViewModel();
        editor.LoadSnapshot(
            CreateSnapshot(hasMinecraft: hasMinecraft),
            true);
        return editor;
    }

    private static MemoryPerformancePolicySnapshot CreateSnapshot(
        long budget = 10 * GiB,
        long available = 8 * GiB,
        bool hasMinecraft = false) =>
        new(
            16 * GiB,
            16 * GiB - available,
            available,
            3 * GiB,
            256 * 1024 * 1024,
            0,
            0,
            0,
            hasMinecraft ? 4 * GiB : 0,
            5 * GiB,
            7 * GiB,
            budget,
            null,
            ResourceMode.Balanced,
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.Normal,
            null,
            null,
            null,
            0,
            false,
            false,
            false,
            [],
            [],
            false,
            true,
            hasMinecraft,
            false,
            false,
            0,
            hasMinecraft
                ? "Active: Balanced · Palworld Normal · Minecraft Normal"
                : "Active: Balanced · Palworld Normal");
}
