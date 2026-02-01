using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace webFilesUno
{
    public class DownloadItemViewModel : INotifyPropertyChanged
    {
        private double _percent;
        private string _statusText;

        public string FileName { get; set; }

        public double Percent
        {
            get => _percent;
            set { _percent = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}