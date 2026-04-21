using System.Windows;

namespace ErneyTranslateTool.Views
{
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
        }

        public void SetTranslation(string text, Rect targetRect)
        {
            TranslationText.Text = text;
            TranslationBorder.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            Left = targetRect.Left;
            Top = targetRect.Bottom + 4;
        }

        public void UpdateBounds(Rect windowRect)
        {
            // При необходимости перемещаем оверлей вслед за окном игры
        }
    }
}
