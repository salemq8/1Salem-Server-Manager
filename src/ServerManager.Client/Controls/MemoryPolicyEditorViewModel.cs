using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client.Controls;

public sealed class MemoryPolicyEditorViewModel :
    INotifyPropertyChanged,
    INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors =
        new(StringComparer.Ordinal);
    private readonly AsyncRelayCommand _saveChangesCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _applyRecommendedCommand;
    private Func<Task>? _saveHandler;
    private MemoryPerformancePolicySnapshot? _snapshot;
    private ResourceMode _selectedMemoryPolicyPreset = ResourceMode.Balanced;
    private string _windowsReserveGiB = string.Empty;
    private string _warningThresholdGiB = string.Empty;
    private string _criticalThresholdGiB = string.Empty;
    private string _totalServerBudgetGiB = string.Empty;
    private bool _notifyAtCriticalThreshold;
    private bool _autoSaveAndRestart;
    private bool _allowUnsafeStartupOverride;
    private bool _hasUnsavedChanges;
    private bool _suppressDirty;

    public MemoryPolicyEditorViewModel()
    {
        SelectPresetCommand = new RelayCommand(parameter =>
        {
            if (TryParseMode(parameter, out var mode))
            {
                SelectedMemoryPolicyPreset = mode;
                PresetSelectionRequested?.Invoke(this, mode);
            }
        });
        _saveChangesCommand = new AsyncRelayCommand(
            async () =>
            {
                if (_saveHandler is not null)
                {
                    await _saveHandler();
                }
            },
            () => HasUnsavedChanges && !HasErrors);
        _cancelCommand = new RelayCommand(
            _ => Cancel(),
            () => HasUnsavedChanges);
        _applyRecommendedCommand = new RelayCommand(
            _ => ApplyRecommended(),
            () => IsCustomPolicy && _snapshot is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public event EventHandler<ResourceMode>? PresetSelectionRequested;

    public ICommand SelectPresetCommand { get; }

    public ICommand SaveChangesCommand => _saveChangesCommand;

    public ICommand CancelCommand => _cancelCommand;

    public ICommand ApplyRecommendedCommand => _applyRecommendedCommand;

    public ResourceMode SelectedMemoryPolicyPreset
    {
        get => _selectedMemoryPolicyPreset;
        set
        {
            if (_selectedMemoryPolicyPreset == value)
            {
                return;
            }

            _selectedMemoryPolicyPreset = value;
            MarkDirty();
            RaiseSelectionProperties();
            ValidateAll();
        }
    }

    public bool IsSafeSelected =>
        SelectedMemoryPolicyPreset == ResourceMode.Safe;

    public bool IsBalancedSelected =>
        SelectedMemoryPolicyPreset == ResourceMode.Balanced;

    public bool IsPerformanceSelected =>
        SelectedMemoryPolicyPreset == ResourceMode.Performance;

    public bool IsCustomSelected =>
        SelectedMemoryPolicyPreset == ResourceMode.Custom;

    public bool IsOneGameAtATimeSelected =>
        SelectedMemoryPolicyPreset == ResourceMode.OneGameAtATime;

    public bool IsCustomPolicy =>
        SelectedMemoryPolicyPreset == ResourceMode.Custom;

    public bool CanEditMemoryThresholds => IsCustomPolicy;

    public bool MemoryFieldsReadOnly => !CanEditMemoryThresholds;

    public bool HasMinecraftServer => _snapshot?.HasMinecraftServer == true;

    public bool HasPalworldServer => _snapshot?.HasPalworldServer == true;

    public string WindowsReserveGiB
    {
        get => _windowsReserveGiB;
        set => SetEditorText(
            ref _windowsReserveGiB,
            value,
            nameof(WindowsReserveGiB));
    }

    public string WarningThresholdGiB
    {
        get => _warningThresholdGiB;
        set => SetEditorText(
            ref _warningThresholdGiB,
            value,
            nameof(WarningThresholdGiB));
    }

    public string CriticalThresholdGiB
    {
        get => _criticalThresholdGiB;
        set => SetEditorText(
            ref _criticalThresholdGiB,
            value,
            nameof(CriticalThresholdGiB));
    }

    public string TotalServerBudgetGiB
    {
        get => _totalServerBudgetGiB;
        set => SetEditorText(
            ref _totalServerBudgetGiB,
            value,
            nameof(TotalServerBudgetGiB));
    }

    public bool NotifyAtCriticalThreshold
    {
        get => _notifyAtCriticalThreshold;
        set => SetEditorBoolean(
            ref _notifyAtCriticalThreshold,
            value,
            nameof(NotifyAtCriticalThreshold));
    }

    public bool AutoSaveAndRestart
    {
        get => _autoSaveAndRestart;
        set => SetEditorBoolean(
            ref _autoSaveAndRestart,
            value,
            nameof(AutoSaveAndRestart));
    }

    public bool AllowUnsafeStartupOverride
    {
        get => _allowUnsafeStartupOverride;
        set => SetEditorBoolean(
            ref _allowUnsafeStartupOverride,
            value,
            nameof(AllowUnsafeStartupOverride));
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (_hasUnsavedChanges == value)
            {
                return;
            }

            _hasUnsavedChanges = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnsavedChangesText));
            OnPropertyChanged(nameof(PendingValuesSummary));
            RaiseCommandStates();
        }
    }

    public string UnsavedChangesText =>
        HasUnsavedChanges ? "Unsaved changes." : "All changes saved.";

    public string ActiveProfileSummary
    {
        get
        {
            if (_snapshot is null)
            {
                return $"Active: {SelectedMemoryPolicyPreset} · No managed game servers";
            }

            var policy = BuildDisplayPolicy();
            var games = new List<GameType>();
            if (_snapshot.HasPalworldServer)
            {
                games.Add(GameType.Palworld);
            }

            if (_snapshot.HasMinecraftServer)
            {
                games.Add(GameType.Minecraft);
            }

            return ResourceProfileSummary.Build(policy, games);
        }
    }

    public string ActiveValuesSummary =>
        _snapshot is null
            ? "Active values unavailable."
            : $"Reserve {FormatGiB(_snapshot.WindowsReserveBytes)} GiB · " +
              $"Warning {FormatGiB(_snapshot.WarningThresholdBytes ?? 0)} GiB · " +
              $"Critical {FormatGiB(_snapshot.CriticalThresholdBytes ?? 0)} GiB · " +
              $"Budget {FormatGiB(_snapshot.MaximumServerBudgetBytes)} GiB";

    public string PendingValuesSummary =>
        $"Reserve {WindowsReserveGiB} GiB · Warning {WarningThresholdGiB} GiB · " +
        $"Critical {CriticalThresholdGiB} GiB · Budget {TotalServerBudgetGiB} GiB" +
        (HasUnsavedChanges ? " · pending" : string.Empty);

    public string CapacityCalculation
    {
        get
        {
            if (_snapshot is null)
            {
                return "Capacity calculation unavailable.";
            }

            if (!TryParsePositiveGiB(WindowsReserveGiB, out var reserve) ||
                !TryParsePositiveGiB(TotalServerBudgetGiB, out var budget))
            {
                return $"Total physical RAM: {FormatGiB(_snapshot.TotalSystemMemoryBytes)} GiB.";
            }

            var limit = Math.Max(0, _snapshot.TotalSystemMemoryBytes - reserve);
            return $"Maximum budget = {FormatGiB(_snapshot.TotalSystemMemoryBytes)} GiB total " +
                   $"- {FormatGiB(reserve)} GiB Windows reserve = " +
                   $"{FormatGiB(limit)} GiB. Pending budget: {FormatGiB(budget)} GiB.";
        }
    }

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return _errors.SelectMany(pair => pair.Value).ToArray();
        }

        return _errors.TryGetValue(propertyName, out var errors)
            ? errors
            : Array.Empty<string>();
    }

    public void SetSaveHandler(Func<Task> saveHandler) =>
        _saveHandler = saveHandler ?? throw new ArgumentNullException(nameof(saveHandler));

    public void LoadSnapshot(
        MemoryPerformancePolicySnapshot snapshot,
        bool forceEditorValues = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        if (forceEditorValues || !HasUnsavedChanges)
        {
            _suppressDirty = true;
            try
            {
                _selectedMemoryPolicyPreset = snapshot.ActiveProfile;
                _windowsReserveGiB = FormatGiB(snapshot.WindowsReserveBytes);
                _warningThresholdGiB =
                    FormatGiB(snapshot.WarningThresholdBytes ?? 0);
                _criticalThresholdGiB =
                    FormatGiB(snapshot.CriticalThresholdBytes ?? 0);
                _totalServerBudgetGiB =
                    FormatGiB(snapshot.MaximumServerBudgetBytes);
                _notifyAtCriticalThreshold =
                    snapshot.CriticalNotificationEnabled;
                _autoSaveAndRestart = snapshot.AutoSaveAndRestart;
                _allowUnsafeStartupOverride =
                    snapshot.AllowUnsafeStartupOverride;
                HasUnsavedChanges = false;
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        RaiseSelectionProperties();
        foreach (var property in new[]
                 {
                     nameof(WindowsReserveGiB),
                     nameof(WarningThresholdGiB),
                     nameof(CriticalThresholdGiB),
                     nameof(TotalServerBudgetGiB),
                     nameof(NotifyAtCriticalThreshold),
                     nameof(AutoSaveAndRestart),
                     nameof(AllowUnsafeStartupOverride),
                     nameof(HasMinecraftServer),
                     nameof(HasPalworldServer),
                     nameof(ActiveValuesSummary),
                     nameof(PendingValuesSummary),
                     nameof(CapacityCalculation)
                 })
        {
            OnPropertyChanged(property);
        }

        ValidateAll();
    }

    public bool TryBuildRequest(
        out ResourceProfileRequest request,
        out string error)
    {
        ValidateAll();
        if (HasErrors ||
            !TryParsePositiveGiB(WindowsReserveGiB, out var reserve) ||
            !TryParsePositiveGiB(WarningThresholdGiB, out var warning) ||
            !TryParsePositiveGiB(CriticalThresholdGiB, out var critical) ||
            !TryParsePositiveGiB(TotalServerBudgetGiB, out var budget))
        {
            request = new ResourceProfileRequest(SelectedMemoryPolicyPreset);
            error = _errors.Values.SelectMany(item => item).FirstOrDefault() ??
                    "Enter valid memory values.";
            return false;
        }

        request = new ResourceProfileRequest(
            SelectedMemoryPolicyPreset,
            reserve,
            budget,
            PalworldWarningThresholdBytes: warning,
            PalworldCriticalThresholdBytes: critical,
            CriticalNotificationEnabled: NotifyAtCriticalThreshold,
            AutoSaveAndRestart: AutoSaveAndRestart,
            AllowUnsafeStartupOverride: AllowUnsafeStartupOverride);
        error = string.Empty;
        return true;
    }

    public void MarkSaved(MemoryPerformancePolicySnapshot snapshot) =>
        LoadSnapshot(snapshot, true);

    public void Cancel()
    {
        if (_snapshot is not null)
        {
            LoadSnapshot(_snapshot, true);
        }
    }

    public void ApplyRecommended()
    {
        if (_snapshot is null || !IsCustomPolicy)
        {
            return;
        }

        var total = _snapshot.TotalSystemMemoryBytes;
        var reserve = Math.Max(
            4 * ResourcePolicyCatalog.Gibibyte,
            total / 4);
        var budget = Math.Min(
            total - reserve,
            total / 2);
        var warning = budget * 2 / 3;
        var critical = budget * 9 / 10;
        WindowsReserveGiB = FormatGiB(reserve);
        WarningThresholdGiB = FormatGiB(warning);
        CriticalThresholdGiB = FormatGiB(critical);
        TotalServerBudgetGiB = FormatGiB(budget);
    }

    private void ValidateAll()
    {
        _errors.Clear();
        if (_snapshot is null)
        {
            RaiseErrorsChanged();
            return;
        }

        ValidatePositive(nameof(WindowsReserveGiB), WindowsReserveGiB, "Windows reserve");
        ValidatePositive(nameof(WarningThresholdGiB), WarningThresholdGiB, "Warning threshold");
        ValidatePositive(nameof(CriticalThresholdGiB), CriticalThresholdGiB, "Critical threshold");
        ValidatePositive(nameof(TotalServerBudgetGiB), TotalServerBudgetGiB, "Total server budget");

        var hasReserve =
            TryParsePositiveGiB(WindowsReserveGiB, out var reserve);
        var hasWarning =
            TryParsePositiveGiB(WarningThresholdGiB, out var warning);
        var hasCritical =
            TryParsePositiveGiB(CriticalThresholdGiB, out var critical);
        var hasBudget =
            TryParsePositiveGiB(TotalServerBudgetGiB, out var budget);
        if (hasWarning && hasCritical && critical < warning)
        {
            AddError(
                nameof(CriticalThresholdGiB),
                "Critical threshold must be greater than or equal to the warning threshold.");
        }

        if (hasWarning && hasBudget && warning > budget)
        {
            AddError(
                nameof(WarningThresholdGiB),
                "Warning threshold cannot exceed the total server budget.");
        }

        if (hasCritical && hasBudget && critical > budget)
        {
            AddError(
                nameof(CriticalThresholdGiB),
                "Critical threshold cannot exceed the total server budget.");
        }

        if (hasReserve &&
            hasBudget &&
            reserve + budget >
            _snapshot.TotalSystemMemoryBytes +
            ResourcePolicyCatalog.DisplayRoundingToleranceBytes)
        {
            var limit = Math.Max(
                0,
                _snapshot.TotalSystemMemoryBytes - reserve);
            AddError(
                nameof(TotalServerBudgetGiB),
                $"Total server budget cannot exceed {FormatGiB(limit)} GiB " +
                $"after the Windows reserve.");
        }

        RaiseErrorsChanged();
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(CapacityCalculation));
        RaiseCommandStates();
    }

    private void ValidatePositive(
        string propertyName,
        string text,
        string label)
    {
        if (!TryParsePositiveGiB(text, out _))
        {
            AddError(
                propertyName,
                $"{label} must be a decimal GiB value greater than zero.");
        }
    }

    private void AddError(string propertyName, string message)
    {
        if (!_errors.TryGetValue(propertyName, out var errors))
        {
            errors = [];
            _errors[propertyName] = errors;
        }

        errors.Add(message);
    }

    private ResourcePolicy BuildDisplayPolicy()
    {
        if (_snapshot is null)
        {
            return ResourcePolicyCatalog.Create(
                SelectedMemoryPolicyPreset,
                8 * ResourcePolicyCatalog.Gibibyte);
        }

        try
        {
            return ResourcePolicyCatalog.Create(
                SelectedMemoryPolicyPreset,
                _snapshot.TotalSystemMemoryBytes,
                TryParsePositiveGiB(WindowsReserveGiB, out var reserve)
                    ? reserve
                    : _snapshot.WindowsReserveBytes,
                TryParsePositiveGiB(TotalServerBudgetGiB, out var budget)
                    ? budget
                    : _snapshot.MaximumServerBudgetBytes,
                palworldWarningThresholdBytes:
                    TryParsePositiveGiB(WarningThresholdGiB, out var warning)
                        ? warning
                        : _snapshot.WarningThresholdBytes,
                palworldCriticalThresholdBytes:
                    TryParsePositiveGiB(CriticalThresholdGiB, out var critical)
                        ? critical
                        : _snapshot.CriticalThresholdBytes,
                allowUnsafeStartupOverride:
                    AllowUnsafeStartupOverride);
        }
        catch (ArgumentException)
        {
            return new ResourcePolicy(
                SelectedMemoryPolicyPreset,
                ProcessPriorityClass.Normal,
                SelectedMemoryPolicyPreset == ResourceMode.Performance
                    ? ProcessPriorityClass.AboveNormal
                    : ProcessPriorityClass.Normal,
                _snapshot.WindowsReserveBytes,
                _snapshot.MaximumServerBudgetBytes);
        }
    }

    private void SetEditorText(
        ref string field,
        string value,
        string propertyName)
    {
        value ??= string.Empty;
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        MarkDirty();
        OnPropertyChanged(nameof(PendingValuesSummary));
        ValidateAll();
    }

    private void SetEditorBoolean(
        ref bool field,
        bool value,
        string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_suppressDirty)
        {
            HasUnsavedChanges = true;
        }
    }

    private void RaiseSelectionProperties()
    {
        foreach (var property in new[]
                 {
                     nameof(SelectedMemoryPolicyPreset),
                     nameof(IsSafeSelected),
                     nameof(IsBalancedSelected),
                     nameof(IsPerformanceSelected),
                     nameof(IsCustomSelected),
                     nameof(IsOneGameAtATimeSelected),
                     nameof(IsCustomPolicy),
                     nameof(CanEditMemoryThresholds),
                     nameof(MemoryFieldsReadOnly),
                     nameof(ActiveProfileSummary),
                     nameof(PendingValuesSummary)
                 })
        {
            OnPropertyChanged(property);
        }

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _saveChangesCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _applyRecommendedCommand.RaiseCanExecuteChanged();
    }

    private void RaiseErrorsChanged()
    {
        foreach (var property in new[]
                 {
                     nameof(WindowsReserveGiB),
                     nameof(WarningThresholdGiB),
                     nameof(CriticalThresholdGiB),
                     nameof(TotalServerBudgetGiB)
                 })
        {
            ErrorsChanged?.Invoke(
                this,
                new DataErrorsChangedEventArgs(property));
        }
    }

    private static bool TryParseMode(object? value, out ResourceMode mode) =>
        value switch
        {
            ResourceMode resourceMode => Return(resourceMode, out mode),
            string text when Enum.TryParse(text, true, out ResourceMode parsed) =>
                Return(parsed, out mode),
            _ => Return(default, out mode, false)
        };

    private static bool Return(
        ResourceMode value,
        out ResourceMode mode,
        bool result = true)
    {
        mode = value;
        return result;
    }

    public static bool TryParsePositiveGiB(string text, out long bytes)
    {
        var styles = NumberStyles.Float;
        if ((double.TryParse(
                 text,
                 styles,
                 CultureInfo.CurrentCulture,
                 out var value) ||
             double.TryParse(
                 text,
                 styles,
                 CultureInfo.InvariantCulture,
                 out value)) &&
            double.IsFinite(value) &&
            value > 0 &&
            value <= long.MaxValue / (double)ResourcePolicyCatalog.Gibibyte)
        {
            bytes = checked((long)Math.Round(
                value * ResourcePolicyCatalog.Gibibyte,
                MidpointRounding.AwayFromZero));
            return bytes > 0;
        }

        bytes = 0;
        return false;
    }

    private static string FormatGiB(long bytes) =>
        (bytes / (double)ResourcePolicyCatalog.Gibibyte)
        .ToString("0.00", CultureInfo.CurrentCulture);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand(
        Action<object?> execute,
        Func<bool>? canExecute = null) :
        ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null) :
        ICommand
    {
        private bool _executing;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !_executing && (canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _executing = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            finally
            {
                _executing = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
