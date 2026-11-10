using Avalonia.Controls;
using FluxMedia.Avalonia;

var control = new FluxPlayerView();
Console.WriteLine(
    $"{typeof(Control).Assembly.GetName().Version}|{control.GetType().Assembly.GetName().Version}");
