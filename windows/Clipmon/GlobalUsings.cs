// WPF + WinForms hybrid: NotifyIcon comes from WinForms, everything else is WPF.
// Resolve every ambiguous type to the WPF version so code reads naturally.

global using System.IO;

global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using DataObject = System.Windows.DataObject;
global using DataFormats = System.Windows.DataFormats;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DragEventArgs = System.Windows.DragEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Key = System.Windows.Input.Key;
global using ModifierKeys = System.Windows.Input.ModifierKeys;
global using Keyboard = System.Windows.Input.Keyboard;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using Binding = System.Windows.Data.Binding;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
global using Brushes = System.Windows.Media.Brushes;
