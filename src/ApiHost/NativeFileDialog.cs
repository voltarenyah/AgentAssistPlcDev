using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

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
        var selectedPath = new StringBuilder(32_768);
        var dialog = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Filter = "TIA Portal project (*.ap17)\0*.ap17\0All files (*.*)\0*.*\0\0",
            FilterIndex = 1,
            File = selectedPath,
            MaxFile = selectedPath.Capacity,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Title = "Select a TIA Portal project",
            Flags = OfnExplorer | OfnFileMustExist | OfnHideReadOnly | OfnNoChangeDir | OfnPathMustExist,
        };

        return GetOpenFileName(ref dialog) ? selectedPath.ToString() : null;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public StringBuilder File;
        public int MaxFile;
        public StringBuilder? FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int Reserved2;
        public int FlagsEx;
    }
}
