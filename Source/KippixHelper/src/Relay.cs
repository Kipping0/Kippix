using System;
using System.ComponentModel;
using System.Windows.Input;

namespace KippixHelper
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object> run;

        public RelayCommand(Action<object> run)
        {
            this.run = run;
        }

        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            try { run(parameter); }
            catch (Exception ex) { Playnite.SDK.LogManager.GetLogger().Error(ex, "Kippix command failed"); }
        }
    }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Notify(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return false;
            field = value;
            Notify(name);
            return true;
        }
    }
}
