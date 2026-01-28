using System.Windows;

namespace PixtechApplication
{
    public partial class LabelInputDialog : Window
    {
        public string CharLabel { get; set; }

        public LabelInputDialog()
        {
            InitializeComponent();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            CharLabel = txtLabel.Text;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(CharLabel))
            {
                txtLabel.Text = CharLabel;
                txtLabel.SelectAll();
            }
            txtLabel.Focus();
        }
    }
}                  