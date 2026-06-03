// Disambiguate WPF types vs System.Windows.Forms types (both referenced via UseWindowsForms=true)
global using Application = System.Windows.Application;
global using Binding = System.Windows.Data.Binding;
global using Button = System.Windows.Controls.Button;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
