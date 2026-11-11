using Avalonia;
using Avalonia.Styling;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;

namespace Sshelfy.App.Services;

internal sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public AppTheme Current => _settings.Current.Theme;

    public bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public event EventHandler? Changed;

    public void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        _settings.Update(s => s with { Theme = theme });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
