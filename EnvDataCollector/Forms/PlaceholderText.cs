// PlaceholderTextBox.cs
using System;
using System.Drawing;
using System.Windows.Forms;

namespace EnvDataCollector
{
    public class PlaceholderTextBox : TextBox
    {
        private string _placeholderText = "";
        private Color _placeholderColor = SystemColors.GrayText;
        private bool _isPlaceholderActive = false;

        public string PlaceholderText
        {
            get => _placeholderText;
            set
            {
                _placeholderText = value;
                SetPlaceholder();
            }
        }

        public Color PlaceholderColor
        {
            get => _placeholderColor;
            set
            {
                _placeholderColor = value;
                if (_isPlaceholderActive)
                    ForeColor = _placeholderColor;
            }
        }

        private void SetPlaceholder()
        {
            if (string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(_placeholderText))
            {
                _isPlaceholderActive = true;
                Text = _placeholderText;
                ForeColor = _placeholderColor;
            }
        }

        private void RemovePlaceholder()
        {
            if (_isPlaceholderActive)
            {
                _isPlaceholderActive = false;
                Text = "";
                ForeColor = SystemColors.WindowText;
            }
        }

        protected override void OnEnter(EventArgs e)
        {
            RemovePlaceholder();
            base.OnEnter(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if (string.IsNullOrEmpty(Text))
            {
                SetPlaceholder();
            }
        }
    }
}