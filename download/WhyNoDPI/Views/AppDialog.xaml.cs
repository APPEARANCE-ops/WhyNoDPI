using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NoDPI.Views;

public enum DialogTone
{
    Info,
    Success,
    Warning,
    Danger
}

public sealed class DialogButton
{
    public DialogButton(string text, bool primary = false, bool danger = false, bool cancel = false)
    {
        Text = text;
        Primary = primary;
        Danger = danger;
        Cancel = cancel;
    }

    public string Text { get; }
    public bool Primary { get; }
    public bool Danger { get; }
    public bool Cancel { get; }
}

/// <summary>
/// Modal dialog that matches the application theme. The previous build used the
/// stock WPF MessageBox, which renders as a bright system window inside a dark
/// application. Every question and error now uses this dialog instead.
/// </summary>
public partial class AppDialog : Window
{
    private int result = -1;
    private int defaultIndex = -1;
    private bool closing;

    private AppDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog and returns the index of the pressed button, or -1.</summary>
    public static int Show(Window? owner, string title, string message, DialogTone tone,
        IReadOnlyList<DialogButton> buttons, string? subtitle = null)
    {
        var dialog = new AppDialog();
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            dialog.SubtitleText.Text = subtitle;
            dialog.SubtitleText.Visibility = Visibility.Visible;
        }

        dialog.ApplyTone(tone);
        dialog.BuildButtons(buttons);
        dialog.ShowDialog();
        return dialog.result;
    }

    public static void Info(Window? owner, string title, string message) =>
        Show(owner, title, message, DialogTone.Info, new[] { new DialogButton("Понятно", primary: true) });

    public static void Success(Window? owner, string title, string message) =>
        Show(owner, title, message, DialogTone.Success, new[] { new DialogButton("Готово", primary: true) });

    public static void Warning(Window? owner, string title, string message) =>
        Show(owner, title, message, DialogTone.Warning, new[] { new DialogButton("Понятно", primary: true) });

    public static void Error(Window? owner, string title, string message) =>
        Show(owner, title, message, DialogTone.Danger, new[] { new DialogButton("Закрыть", primary: true) });

    public static bool Confirm(Window? owner, string title, string message,
        string confirmText = "Продолжить", string cancelText = "Отмена", bool danger = false)
    {
        int index = Show(owner, title, message, danger ? DialogTone.Warning : DialogTone.Info,
            new[]
            {
                new DialogButton(cancelText, cancel: true),
                new DialogButton(confirmText, primary: true, danger: danger)
            });
        return index == 1;
    }

    private void ApplyTone(DialogTone tone)
    {
        (string glyph, Brush accent, Brush background) = tone switch
        {
            DialogTone.Success => ("✓", Hex(0x35, 0xD4, 0x99), Hex(0x10, 0x2E, 0x26)),
            DialogTone.Warning => ("!", Hex(0xF5, 0xB9, 0x42), Hex(0x2C, 0x24, 0x13)),
            DialogTone.Danger => ("✕", Hex(0xF1, 0x64, 0x6F), Hex(0x2E, 0x16, 0x20)),
            _ => ("i", Hex(0x4D, 0x8D, 0xFF), Hex(0x16, 0x29, 0x4A))
        };

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = accent;
        IconBadge.Background = background;
    }

    private static Brush Hex(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private void BuildButtons(IReadOnlyList<DialogButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            DialogButton definition = buttons[i];
            var button = new Button
            {
                Content = definition.Text,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                MinWidth = 108
            };

            string styleKey = definition.Primary ? "Btn.Primary" : definition.Danger ? "Btn.Danger" : "ButtonBase";
            if (TryFindResource(styleKey) is Style style)
                button.Style = style;

            int index = i;
            button.Click += (_, _) => CloseWith(index);
            ButtonRow.Children.Add(button);

            if (definition.Primary)
                defaultIndex = i;
        }

        if (defaultIndex < 0 && buttons.Count > 0)
            defaultIndex = 0;
    }

    private void CloseWith(int index)
    {
        if (closing)
            return;
        closing = true;
        result = index;
        DialogResult = index >= 0;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            int cancelIndex = -1;
            for (int i = 0; i < ButtonRow.Children.Count; i++)
            {
                if (ButtonRow.Children[i] is Button { Content: string text } &&
                    text.Contains("Отмена", StringComparison.OrdinalIgnoreCase))
                {
                    cancelIndex = i;
                    break;
                }
            }
            CloseWith(cancelIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && defaultIndex >= 0)
        {
            CloseWith(defaultIndex);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        base.OnClosed(e);
    }
}
