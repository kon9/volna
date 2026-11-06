using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Volna.Core;

namespace Volna.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var prefs = Preferences.Load();
            Loc.Use(prefs.Language);
            desktop.MainWindow = new MainWindow(new YtDlpBackend(), prefs, Program.StartupEntries, checkTools: true);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>A translated string in XAML, looked up by its key.</summary>
public sealed class TrExtension
{
    public TrExtension(string key) => Key = key;
    public string Key { get; set; }
    public string ProvideValue(IServiceProvider services) => Loc.T(Key);
}

public static class Converters
{
    public static readonly IValueConverter Thumb = new ThumbConverter();
}

/// <summary>
/// Previews are kept as small JPEG bytes; only the ones on screen are decoded, and the most recent ones are reused.
/// Holding 1500 decoded previews at once would cost about a third of a gigabyte.
/// </summary>
sealed class ThumbConverter : IValueConverter
{
    const int Capacity = 260;
    readonly Dictionary<byte[], LinkedListNode<KeyValuePair<byte[], Bitmap>>> map = new(ReferenceEqualityComparer.Instance as IEqualityComparer<byte[]> ?? EqualityComparer<byte[]>.Default);
    readonly LinkedList<KeyValuePair<byte[], Bitmap>> order = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes) return null;
        if (map.TryGetValue(bytes, out var node))
        {
            order.Remove(node);
            order.AddFirst(node);
            return node.Value.Value;
        }
        Bitmap image;
        try
        {
            using var stream = new MemoryStream(bytes);
            image = Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return null; // a preview in a format this system cannot read just stays a placeholder
        }
        map[bytes] = order.AddFirst(new KeyValuePair<byte[], Bitmap>(bytes, image));
        // Not disposed: a card on screen may still show it; the collector frees it once nothing does.
        while (order.Count > Capacity)
        {
            map.Remove(order.Last!.Value.Key);
            order.RemoveLast();
        }
        return image;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
