using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SourceGit.Converters
{
    public static class BuildStatusToBrushConverter
    {
        public static readonly FuncValueConverter<Models.BuildStatus, IBrush> Default =
            new FuncValueConverter<Models.BuildStatus, IBrush>(ConvertBuildStatusToBrush);

        private static IBrush ConvertBuildStatusToBrush(Models.BuildStatus status)
        {
            return status switch
            {
                Models.BuildStatus.Success => new SolidColorBrush(Color.Parse("#00AA00")),      // Green
                Models.BuildStatus.Failure => new SolidColorBrush(Color.Parse("#FF0000")),      // Red
                Models.BuildStatus.InProgress => new SolidColorBrush(Color.Parse("#0066FF")),   // Blue
                Models.BuildStatus.Stopped => new SolidColorBrush(Color.Parse("#999999")),      // Gray
                Models.BuildStatus.Unstable => new SolidColorBrush(Color.Parse("#FF9900")),     // Orange
                Models.BuildStatus.Unknown => new SolidColorBrush(Color.Parse("#CCCCCC")),      // Light Gray
                _ => new SolidColorBrush(Colors.Gray),
            };
        }
    }
}
