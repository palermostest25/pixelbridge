using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;
public sealed class CompassPage : UserControl,IPage
{
    private readonly AppController _c;
    private readonly CompassRose _rose=new();
    private readonly TextBlock _heading=new(){FontSize=48,FontWeight=FontWeights.SemiBold},_detail=new(){TextWrapping=TextWrapping.Wrap};
    public CompassPage(AppController c)
    {
        _c=c;var root=new StackPanel();root.Children.Add(new TextBlock{Text="Compass",FontSize=28,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,28)});
        var row=new StackPanel{Orientation=Orientation.Horizontal};_rose.Width=320;_rose.Height=320;row.Children.Add(_rose);
        var values=new StackPanel{Margin=new(32,55,0,0),MaxWidth=320};values.Children.Add(_heading);values.Children.Add(new TextBlock{Text="Magnetic north",FontSize=18,Margin=new(0,10,0,20)});values.Children.Add(_detail);row.Children.Add(values);root.Children.Add(row);
        root.Children.Add(new TextBlock{Text="Hold the phone flat, with its top edge pointing forward. Keep it away from magnets, speakers and metal surfaces. Readings pause when this view is hidden.",TextWrapping=TextWrapping.Wrap,MaxWidth=720,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,24,0,0)});Content=root;
    }
    public void Tick()
    {
        var r=_c.Compass.Reading;_heading.Text=r==null?"Finding north…":$"{r.Heading:0}° {r.Direction}";
        _detail.Text=r==null?"Connect your phone to use its rotation and magnetic sensors.":$"{r.Quality}\n\nPitch {r.Pitch:0.0}°    Roll {r.Roll:0.0}°"+(r.FieldMicrotesla is double f?$"\nMagnetic field {f:0.0} µT":"");
        _rose.Heading=r?.Heading??0;_rose.InvalidateVisual();
    }
}
sealed class CompassRose : FrameworkElement
{
    public double Heading {get;set;}
    protected override void OnRender(DrawingContext dc)
    {
        var center=new Point(ActualWidth/2,ActualHeight/2);double radius=Math.Min(ActualWidth,ActualHeight)/2-3;
        var ink=SystemColors.ControlTextBrush;var accent=new SolidColorBrush(Color.FromRgb(30,130,105));
        dc.DrawEllipse(null,new Pen(SystemColors.GrayTextBrush,.7),center,radius,radius);
        for(int i=0;i<72;i++)
        {
            double angle=(i*5-Heading-90)*Math.PI/180;double size=i%6==0?17:8;
            dc.DrawLine(new Pen(i%18==0?accent:SystemColors.GrayTextBrush,i%6==0?1.5:.7),new(center.X+Math.Cos(angle)*(radius-13),center.Y+Math.Sin(angle)*(radius-13)),new(center.X+Math.Cos(angle)*(radius-13-size),center.Y+Math.Sin(angle)*(radius-13-size)));
        }
        for(int i=0;i<4;i++)
        {
            double angle=(i*90-Heading-90)*Math.PI/180;var text=new FormattedText(new[]{"N","E","S","W"}[i],CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),17,i==0?accent:ink,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text,new(center.X+Math.Cos(angle)*(radius-48)-text.Width/2,center.Y+Math.Sin(angle)*(radius-48)-text.Height/2));
        }
        dc.PushTransform(new RotateTransform(-Heading,center.X,center.Y));
        var needle=new StreamGeometry();using(var g=needle.Open()){g.BeginFigure(new(center.X,center.Y-radius*.53),true,true);g.LineTo(new(center.X+12,center.Y),true,false);g.LineTo(new(center.X-12,center.Y),true,false);}dc.DrawGeometry(accent,null,needle);
        dc.PushTransform(new RotateTransform(180,center.X,center.Y));dc.DrawGeometry(SystemColors.GrayTextBrush,null,needle);dc.Pop();dc.Pop();dc.DrawEllipse(SystemColors.WindowBrush,new Pen(accent,2),center,6,6);
    }
}
