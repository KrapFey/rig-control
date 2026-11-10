namespace RigControl;

/// <summary>Formatting helpers for x:Bind function bindings.</summary>
public static class Fmt
{
    public static string Int(double v) => v.ToString("0");
    public static string Percent(double v) => $"{v:0}%";
    public static string Duty(double v, bool known) => known ? $"{v:0}%" : "–";
    public static string Temp(double v) => $"{v:0.0} °C";
    public static double Fraction(double percent) => Math.Clamp(percent / 100, 0, 1);
    public static double TempFraction(double t) => Math.Clamp(t / 100, 0, 1);
    public static double PresentOpacity(bool present) => present ? 1 : 0.45;
    public static Microsoft.UI.Xaml.Visibility Show(bool v) => v ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public static Microsoft.UI.Xaml.Visibility ShowText(string? s) => Show(!string.IsNullOrEmpty(s));
}
