using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace NetCut.Platforms.Windows;

/// <summary>WinUI keyboard accelerators attached to the native MAUI page root.</summary>
public static class TimelineShortcuts
{
    public static void Attach(FrameworkElement root, Action undo, Action redo,
        Action save, Action togglePlay, Action remove, Action split)
    {
        Add(root, VirtualKey.Z, VirtualKeyModifiers.Control, undo);
        Add(root, VirtualKey.Y, VirtualKeyModifiers.Control, redo);
        Add(root, VirtualKey.S, VirtualKeyModifiers.Control, save);
        Add(root, VirtualKey.Space, VirtualKeyModifiers.None, togglePlay);
        Add(root, VirtualKey.Delete, VirtualKeyModifiers.None, remove);
        Add(root, VirtualKey.B, VirtualKeyModifiers.Control, split);
    }

    private static void Add(FrameworkElement root, VirtualKey key,
        VirtualKeyModifiers modifiers, Action command)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { command(); e.Handled = true; };
        root.KeyboardAccelerators.Add(accelerator);
    }
}
