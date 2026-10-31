using System.Windows.Input;

namespace DriveMonitor.App.ViewModels;

public enum BannerKind
{
    /// <summary>Blue: something happened that needs no action.</summary>
    Info,

    /// <summary>Amber: monitoring cannot work until the user acts (administrator rights).</summary>
    Warning,

    /// <summary>Red: something failed.</summary>
    Error,
}

/// <summary>A message shown above the tables, with an optional action button and a close button.</summary>
public sealed class BannerViewModel : ObservableObject
{
    internal BannerViewModel(
        string key,
        BannerKind kind,
        string message,
        string? actionText,
        Action? action,
        Action<BannerViewModel> close,
        long? expiresAtTick)
    {
        Key = key;
        Kind = kind;
        Message = message;
        ExpiresAtTick = expiresAtTick;
        CloseCommand = new RelayCommand(() => close(this));
        if (actionText is not null && action is not null)
        {
            ActionText = actionText;
            ActionCommand = new RelayCommand(action);
        }
    }

    /// <summary>Identifies the situation; showing a banner with the same key replaces the old one.</summary>
    public string Key { get; }

    public BannerKind Kind { get; }

    public string Message { get; }

    public string? ActionText { get; }

    public ICommand? ActionCommand { get; }

    public ICommand CloseCommand { get; }

    public string Glyph => Kind switch
    {
        BannerKind.Error => "\uEA39",
        BannerKind.Warning => "\uE7BA",
        _ => "\uE946",
    };

    /// <summary><see cref="Environment.TickCount64"/> after which the banner closes by itself, or null.</summary>
    internal long? ExpiresAtTick { get; }
}
