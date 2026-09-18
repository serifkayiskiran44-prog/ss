using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public sealed partial class TrendyolWorkspacePanel
{
    static readonly Brush Ink=new SolidColorBrush(Color.FromRgb(32,55,66));
    static readonly Brush Accent=new SolidColorBrush(Color.FromRgb(23,107,115));
    static readonly Brush Line=new SolidColorBrush(Color.FromRgb(217,226,230));
    void ApplyWorkspaceStyle()
    {
        var button=new Style(typeof(Button));button.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.White));button.Setters.Add(new Setter(Control.ForegroundProperty,Ink));button.Setters.Add(new Setter(Control.BorderBrushProperty,Line));button.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));button.Setters.Add(new Setter(Control.MinHeightProperty,32d));button.Setters.Add(new Setter(Control.CursorProperty,System.Windows.Input.Cursors.Hand));Resources[typeof(Button)]=button;
        var field=new Style(typeof(TextBox));field.Setters.Add(new Setter(Control.BorderBrushProperty,Line));field.Setters.Add(new Setter(Control.MinHeightProperty,30d));field.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));Resources[typeof(TextBox)]=field;
        var combo=new Style(typeof(ComboBox));combo.Setters.Add(new Setter(Control.MinHeightProperty,30d));combo.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));Resources[typeof(ComboBox)]=combo;
    }
    static Button Primary(Button button){button.Background=Accent;button.Foreground=Brushes.White;button.BorderBrush=Accent;return button;}
    static FrameworkElement Section(string title,UIElement content)
    {
        var body=new DockPanel();var heading=new TextBlock{Text=title,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Ink,Margin=new(5,2,5,10)};DockPanel.SetDock(heading,Dock.Top);body.Children.Add(heading);body.Children.Add(content);
        return new Border{BorderBrush=Line,BorderThickness=new(1),Background=Brushes.White,Padding=new(14),Margin=new(5),Child=body};
    }
    static System.Windows.Controls.Grid Columns(UIElement left,UIElement right)
    {
        var grid=new System.Windows.Controls.Grid{Margin=new(4)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.Children.Add(left);System.Windows.Controls.Grid.SetColumn(right,1);grid.Children.Add(right);return grid;
    }
}
