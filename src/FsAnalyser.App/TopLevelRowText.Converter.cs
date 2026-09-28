using System.Globalization;
using System.Windows.Data;
using FsAnalyser.Core;

namespace FsAnalyser.App;

/// <summary>The converter half of <see cref="TopLevelRowText"/>: what the results list binds its two lines through.</summary>
public sealed partial class TopLevelRowText : IValueConverter
{
    /// <summary>The figures line; otherwise the name line.</summary>
    public bool Figures { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not TopLevelRow row ? "" : Figures ? FiguresOf(row) : TitleOf(row);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
