using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ThermalApp.Ui;

/// <summary>
/// Описание элемента управления. Вместо всплывающих подсказок текст показывается
/// в строке внизу окна — не перекрывает интерфейс и не исчезает по таймеру.
///
/// В XAML: ui:Hint.Text="Что делает этот параметр…"
/// Свойство наследуется вниз по дереву, поэтому подсказку можно задать сразу
/// на контейнере (например, на строке «подпись + поле»).
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Hint),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    /// <summary>
    /// Найти подсказку для элемента под курсором: сначала на нём самом,
    /// затем вверх по визуальному дереву.
    /// </summary>
    public static string? Find(DependencyObject? element)
    {
        while (element is not null)
        {
            if (GetText(element) is { Length: > 0 } text) return text;
            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
