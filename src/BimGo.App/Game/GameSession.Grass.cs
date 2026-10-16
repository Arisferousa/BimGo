using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Grass round (build B): grass blades on the grass materials in the Realistic mode. The grass field (where grass
    /// grows, seen from above) is built on a worker thread the first time it's needed and again when the keywords
    /// change; the renderer draws the blades from it each frame. Settings per machine (Appearance → Grass blades).
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        // Settings (saved with the other display choices)
        private bool _grassBlades = true;
        private int _grassDensity = 1;
        private int _grassHeightMm = 120;
        private int _grassDistance = 40;

        // The field: the keywords it was built for (null = never), the build under way and its keywords
        private string _grassBuiltFor;
        private Task<GrassField> _grassTask;
        private string _grassTaskFor;
        private CancellationTokenSource _grassCancel;
        private GrassField _grassField = GrassField.Empty;
        private string _grassError;

        #endregion

        #region Settings

        private void ReadGrassSettings(LaunchSettings settings)
        {
            _grassBlades = settings.GrassBlades;
            _grassDensity = Math.Clamp(settings.GrassDensity, 0, 2);
            _grassHeightMm = Math.Clamp(settings.GrassHeightMm, LaunchSettings.MIN_GRASS_HEIGHT, LaunchSettings.MAX_GRASS_HEIGHT);
            _grassDistance = Math.Clamp(settings.GrassDistance, LaunchSettings.MIN_GRASS_DISTANCE, LaunchSettings.MAX_GRASS_DISTANCE);
        }

        private void WriteGrassSettings(LaunchSettings settings)
        {
            settings.GrassBlades = _grassBlades;
            settings.GrassDensity = _grassDensity;
            settings.GrassHeightMm = _grassHeightMm;
            settings.GrassDistance = _grassDistance;
        }

        #endregion

        #region Field

        /// <summary>True when blades should show this frame (and the field is worth building).</summary>
        private bool GrassWanted => _grassBlades && _realistic && !_whitecard && _renderer != null && _renderer.HasMaterials && _grassError == null;

        /// <summary>
        /// Once a frame: starts a build when the field is missing or the keywords changed, and uploads a finished one.
        /// Allocation-free while nothing changes.
        /// </summary>
        private void UpdateGrass()
        {
            if (!GrassWanted && _grassTask == null) { return; }

            // A finished build: upload it if its keywords are still the ones wanted
            if (_grassTask != null && _grassTask.IsCompleted)
            {
                Task<GrassField> done = _grassTask;
                string doneFor = _grassTaskFor;
                _grassTask = null;
                _grassTaskFor = null;
                _grassCancel?.Dispose();
                _grassCancel = null;
                if (done.Status == TaskStatus.RanToCompletion && doneFor == CurrentGrassKey())
                {
                    _grassField = done.Result ?? GrassField.Empty;
                    _grassBuiltFor = doneFor;
                    string error = _renderer.SetGrassField(_grassField);
                    if (error != null)
                    {
                        _grassError = error;
                        Toast(error, 6f, important: true);
                    }
                }
                else if (done.IsFaulted)
                {
                    _grassError = "Grass blades could not be placed (see the log).";
                    Utilities.Log_Utils.Write($"Grass field failed: {done.Exception?.GetBaseException()}");
                    Toast(_grassError, 5f, important: true);
                }
            }

            if (!GrassWanted) { return; }
            string key = CurrentGrassKey();
            if (key == _grassBuiltFor) { return; }
            if (_grassTask != null)
            {
                // A build for other keywords: stop it (the next frame after it ends starts the right one)
                if (_grassTaskFor != key) { _grassCancel?.Cancel(); }
                return;
            }

            // Start a build (the scene is read-only: safe on a worker thread)
            List<string> keywords = new(_grassKeywords);
            SceneData scene = Scene;
            _grassCancel = new CancellationTokenSource();
            CancellationToken token = _grassCancel.Token;
            _grassTaskFor = key;
            _grassTask = Task.Run(() => GrassField.Build(scene, keywords, token), token);
        }

        /// <summary>The keywords as one string (what a field was built for).</summary>
        private string CurrentGrassKey() => MaterialOverrides.FormatKeywords(_grassKeywords);

        /// <summary>
        /// Draws the blades (main view: after the opaque scene and the ground).
        /// </summary>
        private void DrawGrass(in SceneDrawParams p)
        {
            if (!GrassWanted || !_renderer.HasGrass || _grassBuiltFor != CurrentGrassKey()) { return; }
            _renderer.Grass = new GrassLook
            {
                Density = _grassDensity,
                Height = _grassHeightMm / 1000f,
                Distance = _grassDistance
            };
            _renderer.DrawGrass(p);
        }

        // The status line, made again only when what it says changes (no per-frame strings)
        private string _grassStatusText;
        private int _grassStatusKey = int.MinValue;

        /// <summary>The GRASS page's status line.</summary>
        private string GrassStatus()
        {
            int key = HashCode.Combine(_grassError, _renderer?.HasMaterials ?? false, _realistic && !_whitecard, _grassBlades,
                _grassTask != null || _grassBuiltFor != CurrentGrassKey(), _grassField.GrassCells);
            if (key == _grassStatusKey && _grassStatusText != null) { return _grassStatusText; }
            _grassStatusKey = key;
            return _grassStatusText = MakeGrassStatus();
        }

        private string MakeGrassStatus()
        {
            if (_grassError != null) { return _grassError; }
            if (_renderer == null || !_renderer.HasMaterials) { return "Needs a snapshot with materials (tick Extract materials and textures at Go)."; }
            if (!_realistic || _whitecard) { return "Shows in the Realistic colour mode."; }
            if (!_grassBlades) { return "Off."; }
            if (_grassTask != null || _grassBuiltFor != CurrentGrassKey()) { return "Finding the grass…"; }
            if (_grassField.IsEmpty) { return "No upward surfaces with a grass material: check the keywords against the material names."; }
            float area = _grassField.GrassCells * _grassField.Cell * _grassField.Cell;
            return $"{area:N0} m² of grass in {_grassField.Colours.Length} {(_grassField.Colours.Length == 1 ? "material" : "materials")}. Blades near you only; the surface's texture carries on beyond.";
        }

        #endregion
    }
}
