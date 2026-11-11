using Sshelfy.Core.Settings;

namespace Sshelfy.App.Services;

public interface IThemeService
{
    AppTheme Current { get; }

    /// <summary>True when the effective (resolved) theme is dark.</summary>
    bool IsDark { get; }

    event EventHandler? Changed;

    void Apply(AppTheme theme);
}
