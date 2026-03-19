using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowDesigner;

public enum EdgeKind
{
    Sequence,
    Then,
    Else
}

public sealed class GraphEdgeViewModel : INotifyPropertyChanged
{
    private double _x1, _y1, _x2, _y2;

    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public required EdgeKind Kind { get; init; }

    public double X1 { get => _x1; set => SetField(ref _x1, value); }
    public double Y1 { get => _y1; set => SetField(ref _y1, value); }
    public double X2 { get => _x2; set => SetField(ref _x2, value); }
    public double Y2 { get => _y2; set => SetField(ref _y2, value); }

    public double LabelX => (X1 + X2) / 2;
    public double LabelY => (Y1 + Y2) / 2 - 14;

    public string Label => Kind switch
    {
        EdgeKind.Then => "Then",
        EdgeKind.Else => "Else",
        _ => string.Empty
    };

    public string StrokeColor => Kind switch
    {
        EdgeKind.Then => "#60A5FA",
        EdgeKind.Else => "#F97316",
        _ => "#A3E635"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.01)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LabelX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LabelY)));
    }
}
