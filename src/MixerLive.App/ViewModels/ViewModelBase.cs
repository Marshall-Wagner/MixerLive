using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MixerLive.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Entry in a device ComboBox. The "(none)" entry has a null Id.</summary>
public sealed record DeviceOption(string? Id, string Name, bool IsMissing = false)
{
    public static readonly DeviceOption None = new(null, "(none)");

    public string Display => IsMissing ? $"(not connected) {Name}" : Name;
}

/// <summary>A channel with a fader; lets the window's wheel and arrow-key handlers work on strips and buses alike.</summary>
public interface IGainControl
{
    double GainDb { get; set; }
}

public enum StatusKind
{
    None,
    Ok,
    Waiting,
    Error
}
