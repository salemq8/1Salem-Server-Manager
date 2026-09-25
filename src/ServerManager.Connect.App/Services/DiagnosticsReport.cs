using System.Text;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.Core.Diagnostics;

namespace ServerManager.Connect.App.Services;

/// <summary>
/// Plain-text report for the Diagnostics page and its Copy button. The whole text passes through
/// <see cref="SecretRedactor"/> when it is built, whatever its parts claim about themselves, so
/// nothing shown or copied can carry a key, ticket or invite secret.
/// </summary>
public sealed class DiagnosticsReport
{
    private readonly StringBuilder _text = new();

    public DiagnosticsReport Line(string label, string? value)
    {
        _text.Append(label).Append(": ").AppendLine(string.IsNullOrEmpty(value) ? Text.DiagnosticsNone : value);
        return this;
    }

    public DiagnosticsReport Section(string title, IReadOnlyCollection<string> lines)
    {
        _text.AppendLine().AppendLine(title);
        if (lines.Count == 0)
        {
            _text.Append("  ").AppendLine(Text.DiagnosticsNone);
        }

        foreach (var line in lines)
        {
            _text.Append("  ").AppendLine(line);
        }

        return this;
    }

    public string Build() => SecretRedactor.Redact(_text.ToString());
}
