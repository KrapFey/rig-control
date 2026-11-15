using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl;

/// <summary>
/// The starter setup (see <see cref="StarterSetup"/>) for a fresh install: shows the curves it would make and which
/// fan would follow which, with a preset to choose, and changes nothing until confirmed.
/// </summary>
static class StarterSetupDialog
{
    /// <summary>True when the setup was applied.</summary>
    public static async Task<bool> RunAsync(XamlRoot root)
    {
        var app = App.Instance!;
        var temps = app.Hub.Temps;
        var cpu = Live.CpuSensor(temps);
        var gpu = Live.GpuSensor(temps);
        var liquid = Live.LiquidSensor(temps);
        var fans = Live.Instance.Fans.Where(f => f.Present).Select(f => new StarterFan(f.Id, f.Name, f.Device)).ToList();
        StarterPlan Plan(CoolingPreset preset) => StarterSetup.Plan(cpu?.Id, gpu?.Id, liquid is null ? null : (liquid.Id, liquid.Device),
            fans, preset, id => temps.FirstOrDefault(t => t.Id == id)?.Name);

        if (Plan(CoolingPreset.Balanced).Curves.Count == 0)
        {
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "Nothing to set up from",
                Content = "RigControl didn't find a CPU, GPU or coolant temperature to make curves for. Make a curve on the Cooling page instead.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return false;
        }

        var presetBox = new ComboBox { Header = "Preset", MinWidth = 220, ItemsSource = CoolingPresets.All.Select(p => p.ToString()).ToList(), SelectedIndex = 1 };
        var planText = new StackPanel { Spacing = 4 };
        var caption = (Style)Application.Current.Resources["Caption"];
        void Show()
        {
            var plan = Plan(CoolingPresets.All[Math.Max(0, presetBox.SelectedIndex)]);
            planText.Children.Clear();
            planText.Children.Add(new TextBlock { Text = "CURVES", Style = caption, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var c in plan.Curves)
            {
                var what = c.Kind == CurveKind.Mix ? $"the higher of {string.Join(" and ", c.Inputs)}"
                    : $"{(temps.FirstOrDefault(t => t.Id == c.Source) is { } t ? app.SensorLabel(t) : c.Source)} · full speed at {c.Points[^1].Temp:0}°";
                planText.Children.Add(new TextBlock { Text = $"{c.Name}: {what}", TextWrapping = TextWrapping.Wrap });
            }
            planText.Children.Add(new TextBlock { Text = "FANS", Style = caption, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var f in plan.Fans)
                planText.Children.Add(new TextBlock
                {
                    Text = f.Curve is null ? $"{f.Fan.Name}: left as it is ({f.Why})" : $"{f.Fan.Name} → {f.Curve} ({f.Why})",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = f.Curve is null ? 0.7 : 1,
                });
            if (plan.Fans.Count == 0) planText.Children.Add(new TextBlock { Text = "No spinning fans found: the curves are made, and fans can follow them later.", TextWrapping = TextWrapping.Wrap });
        }
        presetBox.SelectionChanged += (_, _) => Show();
        Show();

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Set up cooling",
            Content = new ScrollViewer
            {
                MaxHeight = 440,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "RigControl makes these curves and has each fan follow one, guessed from where it's connected. Change anything later on the Cooling page.", TextWrapping = TextWrapping.Wrap },
                        presetBox,
                        planText,
                    },
                },
            },
            PrimaryButtonText = "Set up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var preset = CoolingPresets.All[Math.Max(0, presetBox.SelectedIndex)];
        var chosen = Plan(preset);
        StarterSetup.Apply(chosen, app.Config);
        App.Log($"starter setup ({preset}): {chosen.Curves.Count} curves, {chosen.Fans.Count(f => f.Curve is not null)} fans on them");
        app.CoolingProfileChanged(); // apply, save and show it
        return true;
    }
}
