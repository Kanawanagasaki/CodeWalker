using System;
using System.IO;
using System.Text;

namespace CodeWalker.Export
{
    /// <summary>
    /// Lightweight per-export diagnostic logger for the cutscene glTF exporter.
    ///
    /// Purpose: track down why some characters export with a T-pose (no body animation
    /// reaching the bones) or with wrong textures / wrong models, even though the same
    /// characters render correctly inside CodeWalker itself.
    ///
    /// Usage:
    ///   using (var logger = GltfExportLogger.Start())
    ///   {
    ///       GltfExportLogger.Current?.Log("hello");
    ///       CutsceneGltfExporter.Export(...);   // internally calls GltfExportLogger.Current?.Log(...)
    ///   }
    ///
    /// Log files are written to a `logs` folder next to the running exe, one file per
    /// export, named `gltf_export_YYYYMMDD_HHmmss.fff.log` so multiple runs do not
    /// overwrite each other.
    ///
    /// The logger is opt-in: every call site uses `GltfExportLogger.Current?.Log(...)`,
    /// so when no export is in progress there is zero overhead and no behaviour change.
    /// </summary>
    public sealed class GltfExportLogger : IDisposable
    {
        // ── Ambient current instance ─────────────────────────────────────
        // The cutscene export path crosses several static helpers in GltfWriter that
        // we don't want to bloat with an extra parameter on every call, so we expose
        // the active logger as an ambient singleton set/cleared by Start()/Dispose().
        private static GltfExportLogger _current;

        /// <summary>
        /// The logger for the export currently in progress, or null if no export
        /// (or an export started without logging) is running. All call sites use
        /// `GltfExportLogger.Current?.Log(...)` so this is safe to call at any time.
        /// </summary>
        public static GltfExportLogger Current => _current;

        // ── Instance state ────────────────────────────────────────────────
        private readonly StreamWriter _writer;
        private readonly object _lock = new object();
        private readonly string _logFilePath;
        private int _indent;
        private bool _disposed;
        private readonly DateTime _startedAt;

        private GltfExportLogger(string logFilePath)
        {
            _logFilePath = logFilePath;
            _startedAt = DateTime.Now;

            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // AutoFlush=true so logs are durable even if the export crashes the process.
            _writer = new StreamWriter(logFilePath, append: false, encoding: new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            _current = this;
        }

        /// <summary>
        /// Start a new export log. Creates `&lt;exeDir&gt;/logs/gltf_export_&lt;timestamp&gt;.log`
        /// and sets <see cref="Current"/> to the new logger. Caller must Dispose the
        /// returned instance when the export finishes (typically with a `using` block).
        /// </summary>
        public static GltfExportLogger Start()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var logsDir = Path.Combine(exeDir, "logs");
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss.fff");
            var path = Path.Combine(logsDir, $"gltf_export_{timestamp}.log");
            return new GltfExportLogger(path);
        }

        /// <summary>The full path of the log file currently being written.</summary>
        public string LogFilePath => _logFilePath;

        // ── Core API ──────────────────────────────────────────────────────

        /// <summary>Write a single line with the current indentation.</summary>
        public void Log(string message)
        {
            if (_disposed) return;
            lock (_lock)
            {
                WriteIndent();
                _writer.WriteLine(message ?? string.Empty);
            }
        }

        /// <summary>Write a blank separator line.</summary>
        public void Blank()
        {
            if (_disposed) return;
            lock (_lock) _writer.WriteLine();
        }

        /// <summary>
        /// Begin a labelled scope. Prints a header line and increases indentation for
        /// subsequent Log() calls until the matching <see cref="EndScope"/>.
        /// </summary>
        public void BeginScope(string name)
        {
            if (_disposed) return;
            lock (_lock)
            {
                WriteIndent();
                _writer.WriteLine($"=== {name} ===");
                _indent++;
            }
        }

        /// <summary>End the most recent <see cref="BeginScope"/> and print a footer line.</summary>
        public void EndScope()
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (_indent > 0) _indent--;
            }
        }

        /// <summary>Log a labelled key/value pair on a single line, e.g. `Ped name: player_zero`.</summary>
        public void Field(string label, string value)
        {
            Log($"{label}: {value ?? "<null>"}");
        }

        /// <summary>Log a labelled key/value pair where the value is a nullable integer.</summary>
        public void Field(string label, object value)
        {
            var s = value == null ? "<null>" : value.ToString();
            Log($"{label}: {s}");
        }

        /// <summary>Log an exception with full stack trace, indented under the current scope.</summary>
        public void LogException(string context, Exception ex)
        {
            if (_disposed || ex == null) return;
            lock (_lock)
            {
                WriteIndent();
                _writer.WriteLine($"!! EXCEPTION during {context}: {ex.GetType().Name}: {ex.Message}");
                _indent++;
                WriteIndent();
                _writer.WriteLine(ex.StackTrace ?? "<no stack trace>");
                if (ex.InnerException != null)
                {
                    WriteIndent();
                    _writer.WriteLine($"Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                _indent--;
            }
        }

        // ── IDisposable ──────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                try
                {
                    var elapsed = DateTime.Now - _startedAt;
                    WriteIndent();
                    _writer.WriteLine();
                    WriteIndent();
                    _writer.WriteLine($"=== Export log finished — elapsed {elapsed.TotalSeconds:F3}s — file: {_logFilePath} ===");
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch { /* swallow — dispose must not throw */ }
            }

            if (ReferenceEquals(_current, this))
                _current = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private void WriteIndent()
        {
            for (int i = 0; i < _indent; i++)
                _writer.Write("  ");
        }
    }
}
