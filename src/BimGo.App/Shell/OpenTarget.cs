using BimGo.Format;
using BimGo.Game;

// The class belongs to the Shell namespace
namespace BimGo.Shell
{
    /// <summary>
    /// Something the app should open: a .bimgo file, an IFC file (imported as a new, unsaved model), or a live Revit session
    /// (optionally with the pose to resume).
    /// </summary>
    internal sealed class OpenTarget
    {
        /// <summary>The Open dialog's filter: BimGo models and IFC files (Revit LT exports IFC; it can't run the add-in).</summary>
        public const string OPEN_FILTER = "BimGo or IFC model (*.bimgo;*.ifc;*.ifczip)|*.bimgo;*.ifc;*.ifczip|BimGo model (*.bimgo)|*.bimgo|IFC model (*.ifc;*.ifczip)|*.ifc;*.ifczip|All files (*.*)|*.*";

        /// <summary>True for a file the app opens: a .bimgo, or an .ifc / .ifczip to import.</summary>
        public static bool CanOpen(string path) => BimGoFormat.HasExtension(path) || IfcImporter.HasExtension(path);

        /// <summary>A .bimgo or IFC path (file targets).</summary>
        public string Path { get; init; }

        /// <summary>A session id (live targets).</summary>
        public string SessionId { get; init; }

        /// <summary>Where to put the player (reloads), or null.</summary>
        public SessionPose Pose { get; init; }

        /// <summary>Round Z-C: open the file in kiosk mode (walk, fly and look only; idle tour; Ctrl+Shift+Q held leaves).</summary>
        public bool Kiosk { get; init; }

        /// <summary>True for a live session.</summary>
        public bool IsLive => SessionId != null;

        /// <summary>A file target.</summary>
        public static OpenTarget File(string path) => new() { Path = path };

        /// <summary>Round Z-C: a file target in kiosk mode (<c>BimGo.exe --kiosk "file"</c>).</summary>
        public static OpenTarget KioskFile(string path) => new() { Path = path, Kiosk = true };

        /// <summary>A live session target.</summary>
        public static OpenTarget Live(string sessionId, SessionPose pose = null) => new() { SessionId = sessionId, Pose = pose };
    }
}
