using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

internal static class NativeFileDialog
{
    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnHideReadOnly = 0x00000004;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;

    public static string? SelectTiaProject()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The TIA project file picker is only available on Windows.");

        string? selectedPath = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                selectedPath = ShowDialog();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return selectedPath;
    }

    private static string? ShowDialog()
    {
        const int maxPathCharacters = 32_768;
        var filter = Marshal.StringToHGlobalUni("TIA Portal project (*.ap17)\0*.ap17\0All files (*.*)\0*.*\0\0");
        var file = Marshal.StringToHGlobalUni(new string('\0', maxPathCharacters));
        var initialDirectory = Marshal.StringToHGlobalUni(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var title = Marshal.StringToHGlobalUni("Select a TIA Portal project");

        var dialog = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Filter = filter,
            FilterIndex = 1,
            File = file,
            MaxFile = maxPathCharacters,
            InitialDirectory = initialDirectory,
            Title = title,
            Flags = OfnExplorer | OfnFileMustExist | OfnHideReadOnly | OfnNoChangeDir | OfnPathMustExist,
        };

        try
        {
            return GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(dialog.File) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(initialDirectory);
            Marshal.FreeHGlobal(title);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsEx;
    }
}
