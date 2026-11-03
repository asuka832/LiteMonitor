using System.Runtime.InteropServices;
using System.Text;

namespace LiteMonitor.src.UI;

// Shell file drops also work when Explorer and LiteMonitor have different integrity levels.
internal sealed class ShellFileDrop : NativeWindow
{
    private const int DropFiles = 0x0233;
    private readonly Action<string[]> dropped;

    private ShellFileDrop(Control control, Action<string[]> dropped)
    {
        this.dropped = dropped;
        control.HandleCreated += (_, _) => Register(control.Handle);
        control.HandleDestroyed += (_, _) => ReleaseHandle();
        if (control.IsHandleCreated) Register(control.Handle);
    }

    public static void Attach(Control control, Action<string[]> dropped) => _ = new ShellFileDrop(control, dropped);

    private void Register(IntPtr handle)
    {
        AssignHandle(handle);
        ChangeWindowMessageFilterEx(handle, DropFiles, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(handle, 0x0049, 1, IntPtr.Zero); // Shell global-data transfer.
        DragAcceptFiles(handle, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != DropFiles) { base.WndProc(ref m); return; }
        var files = new List<string>();
        try
        {
            uint count = Math.Min(DragQueryFile(m.WParam, uint.MaxValue, null, 0), 64u);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(m.WParam, i, null, 0);
                if (length == 0 || length > 32767) continue;
                var path = new StringBuilder((int)length + 1);
                if (DragQueryFile(m.WParam, i, path, (uint)path.Capacity) > 0) files.Add(path.ToString());
            }
        }
        finally { DragFinish(m.WParam); }
        m.Result = IntPtr.Zero;
        if (files.Count > 0) dropped(files.ToArray());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr filter);
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint size);
    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr drop);
}
