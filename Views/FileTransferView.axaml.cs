using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class FileTransferView : UserControl
{
    public FileTransferView()
    {
        InitializeComponent();

        // Wire up drag-drop events
        var dropZone = this.FindControl<Border>("DropZoneBorder");
        if (dropZone is not null)
        {
            dropZone.AddHandler(DragDrop.DropEvent,       OnDrop);
            dropZone.AddHandler(DragDrop.DragOverEvent,   OnDragOver);
            dropZone.AddHandler(DragDrop.DragEnterEvent,  OnDragEnter);
            dropZone.AddHandler(DragDrop.DragLeaveEvent,  OnDragLeave);
        }
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileTransferViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title           = "Select file to transfer",
            AllowMultiple   = false
        });

        if (files.Count > 0)
        {
            // Pass the IStorageFile directly to handle Android's content:// URIs
            vm.SetFile(files[0]);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (sender is Border b)
            b.BorderBrush = Avalonia.Media.Brushes.Cyan;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border b)
            b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D2D2D"));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is Border b)
            b.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2D2D2D"));

        if (DataContext is not FileTransferViewModel vm) return;

        var files = e.Data.GetFiles()?.ToList();
        if (files is { Count: > 0 })
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
                vm.SetFile(path);
        }
    }
}
