using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicSearch
{
    /// <summary>
    /// Dark-themed drop-in replacement for System.Windows.MessageBox, styled to match the rest of
    /// SonicSearch's borderless/rounded dark UI instead of popping the default white OS dialog.
    /// Use <see cref="Show"/> exactly like MessageBox.Show - same enums, same result type.
    /// </summary>
    public partial class ThemedMessageBox : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;

        private ThemedMessageBox()
        {
            InitializeComponent();
        }

        public static MessageBoxResult Show(Window owner, string message, string title,
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            var box = new ThemedMessageBox { Owner = owner };
            box.txtTitle.Text = title;
            box.txtMessage.Text = message;
            box.ApplyIcon(icon);
            box.BuildButtons(buttons);
            box.ShowDialog();
            return box._result;
        }

        private void ApplyIcon(MessageBoxImage icon)
        {
            System.Windows.Media.Color badgeColor;
            string glyph;
            switch (icon)
            {
                case MessageBoxImage.Warning:
                    badgeColor = System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);
                    glyph = "!";
                    break;
                case MessageBoxImage.Error:
                    badgeColor = System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44);
                    glyph = "✕";
                    break;
                case MessageBoxImage.Question:
                    badgeColor = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);
                    glyph = "?";
                    break;
                case MessageBoxImage.Information:
                    badgeColor = System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6);
                    glyph = "i";
                    break;
                default:
                    iconBadge.Visibility = Visibility.Collapsed;
                    return;
            }
            iconBadge.Background = new SolidColorBrush(badgeColor);
            txtIconGlyph.Text = glyph;
        }

        private void BuildButtons(MessageBoxButton buttons)
        {
            switch (buttons)
            {
                case MessageBoxButton.YesNo:
                    AddButton("Yes", MessageBoxResult.Yes, accent: true);
                    AddButton("No", MessageBoxResult.No, accent: false);
                    break;
                case MessageBoxButton.YesNoCancel:
                    AddButton("Yes", MessageBoxResult.Yes, accent: true);
                    AddButton("No", MessageBoxResult.No, accent: false);
                    AddButton("Cancel", MessageBoxResult.Cancel, accent: false);
                    break;
                case MessageBoxButton.OKCancel:
                    AddButton("OK", MessageBoxResult.OK, accent: true);
                    AddButton("Cancel", MessageBoxResult.Cancel, accent: false);
                    break;
                default:
                    AddButton("OK", MessageBoxResult.OK, accent: true);
                    break;
            }
        }

        private void AddButton(string text, MessageBoxResult result, bool accent)
        {
            var accentBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
            var accentHoverBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x6C, 0xD8));
            var neutralBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x2E));
            var neutralHoverBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x36, 0x36, 0x3C));

            var btn = new Button
            {
                Content = text,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 8, 16, 8),
                MinWidth = 76,
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = System.Windows.Media.Brushes.White,
                Background = accent ? accentBrush : neutralBrush,
                BorderThickness = new Thickness(0)
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, accent ? accentHoverBrush : neutralHoverBrush, "bd"));
            template.Triggers.Add(hoverTrigger);
            btn.Template = template;

            btn.Click += (s, e) => { _result = result; Close(); };
            buttonPanel.Children.Add(btn);

            if (accent)
                btn.Focus();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}
