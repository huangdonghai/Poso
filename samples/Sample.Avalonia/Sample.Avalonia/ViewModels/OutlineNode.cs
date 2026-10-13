using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Poso.Pdf;

namespace Sample.Avalonia.ViewModels;

/// <summary>A bindable wrapper around a <see cref="PdfOutline"/> node.</summary>
public partial class OutlineNode : ViewModelBase
{
    [ObservableProperty]
    private bool _isExpanded = true;

    public OutlineNode(PdfOutline outline)
    {
        Title = outline.Title;
        Destination = outline.Destination;
        PageNumber = outline.Destination?.PageIndex + 1 ?? 0;

        foreach (PdfOutline child in outline.Children)
        {
            Children.Add(new OutlineNode(child));
        }
    }

    public string Title { get; }

    public PdfDestination? Destination { get; }

    /// <summary>One-based page number for display, or 0 when the node has no destination.</summary>
    public int PageNumber { get; }

    public ObservableCollection<OutlineNode> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;
}
