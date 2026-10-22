using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Adm.App.ViewModels;

/// <summary>One entry in the library sidebar.</summary>
public sealed class CategoryItem : INotifyPropertyChanged
{
    private int _count;
    private bool _isSelected;

    public CategoryItem(string name, string key)
    {
        Name = name;
        Key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    /// <summary>Filter key understood by <see cref="MainViewModel"/>.</summary>
    public string Key { get; }

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
