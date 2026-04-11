using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TomatoNoteTimer.Dialogs;

public partial class NotesEditorDialog : Window
{
    public NotesEditorDialog(IReadOnlyList<string> segments)
    {
        InitializeComponent();
        NotesEditorTextBox.Text = string.Join($"{System.Environment.NewLine}---{System.Environment.NewLine}", segments);
    }

    public List<string> Segments { get; private set; } = new();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Segments = NotesEditorTextBox.Text
            .Split("---")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (Segments.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "请至少保留一段便签内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
