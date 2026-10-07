using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Unimportable;

internal sealed class FileDrop : IDisposable
{
    private const int WindowProcedure = -4;
    private const uint DropFiles = 0x233;
    private static readonly List<FileDrop> Hooks = new();
    private readonly ConcurrentQueue<string[]> files;
    private readonly ConcurrentQueue<string> errors;
    private readonly WindowCallback callback;
    private readonly IntPtr procedure;
    private IntPtr window;
    private IntPtr previous;
    private volatile bool enabled;
    private bool disposed;

    internal FileDrop(ConcurrentQueue<string[]> files, ConcurrentQueue<string> errors)
    {
        this.files = files;
        this.errors = errors;
        callback = HandleMessage;
        procedure = Marshal.GetFunctionPointerForDelegate(callback);
    }

    internal void Update(bool accept)
    {
        if (disposed)
        {
            return;
        }

        if (window != IntPtr.Zero && !IsWindow(window))
        {
            window = IntPtr.Zero;
            previous = IntPtr.Zero;
            enabled = false;
            Hooks.Remove(this);
        }

        if (accept && window == IntPtr.Zero)
        {
            Attach();
        }

        if (window != IntPtr.Zero && enabled != accept)
        {
            enabled = accept;
            DragAcceptFiles(window, accept);
        }
    }

    private void Attach()
    {
        using var process = Process.GetCurrentProcess();
        IntPtr found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var id);
            if (id != (uint)process.Id || !IsWindowVisible(handle))
            {
                return true;
            }

            var name = new StringBuilder(128);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString() != "UnityWndClass")
            {
                return true;
            }

            found = handle;
            return false;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
        {
            return;
        }

        SetLastError(0);
        var original = SetProcedure(found, procedure);
        if (original == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        previous = original;
        window = found;
        Hooks.Add(this);
    }

    private IntPtr HandleMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != DropFiles || !enabled)
        {
            return CallWindowProc(previous, handle, message, wParam, lParam);
        }

        try
        {
            var count = DragQueryFile(wParam, uint.MaxValue, null, 0);
            if (count == 0 || count > 100)
            {
                throw new InvalidOperationException("drop up to 100 archives at once");
            }

            var paths = new string[count];
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(wParam, index, null, 0);
                if (length == 0 || length > 32767)
                {
                    throw new InvalidOperationException("invalid dropped file path");
                }

                var path = new StringBuilder((int)length + 1);
                DragQueryFile(wParam, index, path, (uint)path.Capacity);
                paths[index] = path.ToString();
            }

            files.Enqueue(paths);
        }
        catch (Exception error)
        {
            errors.Enqueue(error.Message);
        }
        finally
        {
            DragFinish(wParam);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        disposed = true;
        enabled = false;
        if (window == IntPtr.Zero || !IsWindow(window))
        {
            Hooks.Remove(this);
            return;
        }

        DragAcceptFiles(window, false);
        if (GetProcedure(window) == procedure)
        {
            SetLastError(0);
            if (SetProcedure(window, previous) != IntPtr.Zero)
            {
                Hooks.Remove(this);
                window = IntPtr.Zero;
            }
        }
    }

    private static IntPtr SetProcedure(IntPtr handle, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr(handle, WindowProcedure, value)
        : new IntPtr(SetWindowLong(handle, WindowProcedure, value.ToInt32()));

    private static IntPtr GetProcedure(IntPtr handle) => IntPtr.Size == 8
        ? GetWindowLongPtr(handle, WindowProcedure)
        : new IntPtr(GetWindowLong(handle, WindowProcedure));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowCallback(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr procedure, IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr handle, bool accept);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr drop);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);
}
