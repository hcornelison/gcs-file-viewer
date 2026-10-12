using Avalonia.Data.Converters;
using Avalonia.Media;
using GcsFileViewer.Storage;

namespace GcsFileViewer.Converters;

/// <summary>Maps an <see cref="ItemKind"/> to the icon shape and color shown next to its name.</summary>
public static class ItemIcons
{
    private static readonly Geometry FolderGeometry = Avalonia.Media.Geometry.Parse(
        "M1,4 C1,3 2,2 3,2 L8,2 L10,4 L17,4 C18,4 19,5 19,6 L19,16 C19,17 18,18 17,18 L3,18 C2,18 1,17 1,16 Z");

    private static readonly Geometry FileGeometry = Avalonia.Media.Geometry.Parse(
        "M4,1 L12,1 L17,6 L17,19 L4,19 Z");

    private static readonly Geometry BucketGeometry = Avalonia.Media.Geometry.Parse(
        "M1,3 L19,3 L19,6 L1,6 Z M2.5,7.5 L17.5,7.5 L16,18 L4,18 Z");

    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#E8B339"));
    private static readonly IBrush BucketBrush = new SolidColorBrush(Color.Parse("#4285F4"));
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.Parse("#8A8F98"));

    public static readonly IValueConverter Geometry = new FuncValueConverter<ItemKind, Geometry>(kind => kind switch
    {
        ItemKind.Bucket => BucketGeometry,
        ItemKind.Folder => FolderGeometry,
        _ => FileGeometry,
    });

    public static readonly IValueConverter Brush = new FuncValueConverter<ItemKind, IBrush>(kind => kind switch
    {
        ItemKind.Bucket => BucketBrush,
        ItemKind.Folder => FolderBrush,
        _ => FileBrush,
    });
}
