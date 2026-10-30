using System.Runtime.InteropServices;

// The class belongs to the Native namespace
namespace BimGo.Native
{
    /// <summary>
    /// Site photos round: the Windows folder picker with several folders selectable (IFileOpenDialog with
    /// FOS_PICKFOLDERS | FOS_ALLOWMULTISELECT; WinForms' FolderBrowserDialog picks one). Hand-written interop through the
    /// COM vtable slots, as <see cref="Mp4Writer"/> (slot numbers from the Windows SDK's shobjidl_core.idl). Call on the
    /// STA main thread. Never throws.
    /// </summary>
    internal static unsafe class FolderPicker
    {
        #region COM

        private static readonly Guid CLSID_FILE_OPEN_DIALOG = new(0xDC1C5A9C, 0xE88A, 0x4DDE, 0xA5, 0xA1, 0x60, 0xF8, 0x2A, 0x20, 0xAE, 0xF7);
        private static readonly Guid IID_FILE_OPEN_DIALOG = new(0xD57C7288, 0xD4AD, 0x4768, 0xBE, 0x02, 0x9D, 0x96, 0x95, 0x32, 0xD9, 0x60);
        private static readonly Guid IID_SHELL_ITEM = new(0x43826D1E, 0xE718, 0x42EE, 0xBC, 0x55, 0xA1, 0xE2, 0x61, 0xC3, 0x7B, 0xFE);

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        // IUnknown 0–2, IModalWindow 3, IFileDialog 4–26, IFileOpenDialog 27–28
        private const int SLOT_RELEASE = 2, SLOT_SHOW = 3, SLOT_SET_OPTIONS = 9, SLOT_GET_OPTIONS = 10, SLOT_SET_FOLDER = 12, SLOT_SET_TITLE = 17,
            SLOT_GET_RESULTS = 27;

        // IShellItemArray: GetCount 7, GetItemAt 8; IShellItem: GetDisplayName 5
        private const int SLOT_ARRAY_COUNT = 7, SLOT_ARRAY_ITEM = 8, SLOT_ITEM_NAME = 5;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoTaskMemFree(IntPtr memory);

        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, Guid* iid, IntPtr* item);

        private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

        private static void Release(IntPtr instance)
        {
            if (instance != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, SLOT_RELEASE))(instance); }
        }

        #endregion

        /// <summary>Shows the picker.</summary>
        /// <param name="owner">The owner window.</param>
        /// <param name="title">The dialog's title.</param>
        /// <param name="initialFolder">Where it starts (or null).</param>
        /// <returns>The folders picked; empty when cancelled; null when the picker isn't available (use the single one).</returns>
        public static string[] PickFolders(nint owner, string title, string initialFolder)
        {
            IntPtr dialog = IntPtr.Zero, results = IntPtr.Zero;
            try
            {
                Guid clsid = CLSID_FILE_OPEN_DIALOG, iid = IID_FILE_OPEN_DIALOG;
                if (CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, &iid, &dialog) < 0 || dialog == IntPtr.Zero) { return null; }

                uint options = 0;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, SLOT_GET_OPTIONS))(dialog, &options);
                options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT | FOS_PATHMUSTEXIST;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, SLOT_SET_OPTIONS))(dialog, options) < 0) { return null; }
                fixed (char* text = title ?? string.Empty)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, SLOT_SET_TITLE))(dialog, text);
                }
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    Guid itemId = IID_SHELL_ITEM;
                    IntPtr folder = IntPtr.Zero;
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, &itemId, &folder) >= 0 && folder != IntPtr.Zero)
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, SLOT_SET_FOLDER))(dialog, folder);
                        Release(folder);
                    }
                }

                int shown = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, SLOT_SHOW))(dialog, owner);
                if (shown == ERROR_CANCELLED) { return Array.Empty<string>(); }
                if (shown < 0) { return null; }

                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dialog, SLOT_GET_RESULTS))(dialog, &results) < 0 || results == IntPtr.Zero) { return null; }
                uint count = 0;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(results, SLOT_ARRAY_COUNT))(results, &count);
                var folders = new List<string>((int)Math.Min(count, 1024u));
                for (uint i = 0; i < count && i < 1024; i++)
                {
                    IntPtr item = IntPtr.Zero;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(results, SLOT_ARRAY_ITEM))(results, i, &item) < 0 || item == IntPtr.Zero) { continue; }
                    IntPtr name = IntPtr.Zero;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(item, SLOT_ITEM_NAME))(item, SIGDN_FILESYSPATH, &name) >= 0 && name != IntPtr.Zero)
                    {
                        string path = Marshal.PtrToStringUni(name);
                        if (!string.IsNullOrEmpty(path)) { folders.Add(path); }
                        CoTaskMemFree(name);
                    }
                    Release(item);
                }
                return folders.ToArray();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Folder picker failed: {ex.Message}");
                return null;
            }
            finally
            {
                Release(results);
                Release(dialog);
            }
        }
    }
}
