using System.Windows;
using System.Windows.Media.Imaging;

namespace NtfsRecovery.Gui;

/// <summary>Modal, on-demand file preview (metadata + content) -- opened only when the user double-clicks a row, so it never occupies space in the main window.</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(string fileName, string metadata, BitmapImage? image, string? text, string? unsupportedReason)
    {
        InitializeComponent();
        Title = $"Pré-visualização - {fileName}";
        MetadataTextControl.Text = metadata;

        PreviewImageControl.Visibility = Visibility.Collapsed;
        PreviewTextControl.Visibility = Visibility.Collapsed;
        UnsupportedTextBlock.Visibility = Visibility.Collapsed;

        if (image is not null)
        {
            PreviewImageControl.Source = image;
            PreviewImageControl.Visibility = Visibility.Visible;
        }
        else if (text is not null)
        {
            PreviewTextControl.Text = text;
            PreviewTextControl.Visibility = Visibility.Visible;
        }
        else
        {
            UnsupportedTextBlock.Text = unsupportedReason ?? "Sem pré-visualização disponível.";
            UnsupportedTextBlock.Visibility = Visibility.Visible;
        }
    }
}
