using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SharpDX;

namespace CodeWalker.Rendering
{
    /// <summary>
    /// Per-frame diagnostic logger for eye bones, with full cutscene/ped/time context.
    ///
    /// USAGE (environment variables, read once on first log write):
    ///   CODEWALKER_EYE_CUTSCENE=car_5_ext   Substring match on cutscene short name (case-insensitive)
    ///   CODEWALKER_EYE_PED=molly             Substring match on ped model name (case-insensitive)
    ///   CODEWALKER_EYE_TMIN=29               Only log frames at or after this cutscene time (seconds)
    ///   CODEWALKER_EYE_TMAX=32               Only log frames at or before this cutscene time (seconds)
    ///   CODEWALKER_EYE_MAXFRAMES=10000       Max frames to log (0 = unlimited)
    ///   CODEWALKER_EYE_DISABLE=1             Disable logging entirely
    ///
    /// CRITICAL: The log file is ALWAYS created on the first frame, even if no env vars
    /// are set or filters reject everything. The top of the file contains:
    ///   1. STARTUP BANNER — env vars read, parsed filter values, log file path
    ///   2. DISCOVERY section — every unique (cutscene, ped) combo actually seen, with
    ///      the REAL name strings. Copy-paste these into your filter env vars.
    ///   3. REJECTIONS section — why frames were rejected (which filter failed)
    ///   4. FRAME blocks — detailed eye bone data for frames that PASSED all filters
    ///
    /// If you see no FRAME blocks, read the DISCOVERY and REJECTIONS sections to find
    /// out what the actual names are and adjust your env vars accordingly.
    ///
    /// Output: codewalker_eye_debug.log in the process working directory.
    /// </summary>
    public static class EyeDebugLog
    {
        // ── Per-frame context (set by caller before/during render) ──
        public static string CutsceneName = "";
        public static string PedName = "";
        public static float CutsceneTime = 0f;
        public static float CutsceneDuration = 0f;

        /// <summary>
        /// Clear all context fields. Called at the start of each render frame
        /// (from WorldForm.RenderScene or similar) so that non-cutscene renderables
        /// don't inherit stale context from the previous frame.
        /// Without this, a world-object BeginFrame call that happens BEFORE
        /// Cutscene.Render() sets the context would log with the PREVIOUS frame's
        /// cutscene name + ped name, producing misleading discovery entries.
        /// </summary>
        public static void ResetFrameContext()
        {
            CutsceneName = "";
            PedName = "";
            CutsceneTime = 0f;
            // Don't reset CutsceneDuration — it's only set when a cutscene is loaded
        }

        // ── Filters (read from env vars on first use, then cached) ──
        private static string _fCutscene;
        private static string _fPed;
        private static float _fTMin = float.MinValue;
        private static float _fTMax = float.MaxValue;
        private static int _maxFrames = 10000;
        private static bool _disabled = false;
        private static bool _filtersLoaded = false;

        // ── Session state ──
        private static readonly object _lock = new object();
        private static string _logPath;
        private static int _frameCounter;
        private static bool _bannerWritten;
        private static readonly List<string> _frameBuffer = new List<string>();

        // Discovery: track unique (cutscene, ped) combos seen
        private static readonly Dictionary<string, (string cutscene, string ped, float firstTime)> _discovered
            = new Dictionary<string, (string, string, float)>();

        // Rejection tracking: count how many frames each filter rejected
        private static int _rejectedByCutscene;
        private static int _rejectedByPed;
        private static int _rejectedByTime;
        private static int _rejectedByNoEyeBones;
        private static int _totalBeginFrameCalls;

        private static void LoadFilters()
        {
            if (_filtersLoaded) return;
            _filtersLoaded = true;

            _fCutscene = (Environment.GetEnvironmentVariable("CODEWALKER_EYE_CUTSCENE") ?? "").Trim();
            _fPed = (Environment.GetEnvironmentVariable("CODEWALKER_EYE_PED") ?? "").Trim();

            string tmin = Environment.GetEnvironmentVariable("CODEWALKER_EYE_TMIN");
            if (!string.IsNullOrEmpty(tmin) && float.TryParse(tmin, NumberStyles.Float, CultureInfo.InvariantCulture, out var tminVal))
                _fTMin = tminVal;

            string tmax = Environment.GetEnvironmentVariable("CODEWALKER_EYE_TMAX");
            if (!string.IsNullOrEmpty(tmax) && float.TryParse(tmax, NumberStyles.Float, CultureInfo.InvariantCulture, out var tmaxVal))
                _fTMax = tmaxVal;

            string mf = Environment.GetEnvironmentVariable("CODEWALKER_EYE_MAXFRAMES");
            if (!string.IsNullOrEmpty(mf) && int.TryParse(mf, out var mfVal))
                _maxFrames = mfVal;

            string dis = Environment.GetEnvironmentVariable("CODEWALKER_EYE_DISABLE");
            if (dis == "1" || string.Equals(dis, "true", StringComparison.OrdinalIgnoreCase))
                _disabled = true;
        }

        /// <summary>
        /// Returns (passes, reason) for the current context.
        /// </summary>
        private static (bool passes, string reason) CheckFilter()
        {
            if (!string.IsNullOrEmpty(_fCutscene))
            {
                if (string.IsNullOrEmpty(CutsceneName) ||
                    CutsceneName.IndexOf(_fCutscene, StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, "cutscene");
            }

            if (!string.IsNullOrEmpty(_fPed))
            {
                if (string.IsNullOrEmpty(PedName) ||
                    PedName.IndexOf(_fPed, StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, "ped");
            }

            if (CutsceneTime < _fTMin || CutsceneTime > _fTMax)
                return (false, "time");

            return (true, null);
        }

        /// <summary>
        /// Ensures the log file exists and the startup banner has been written.
        /// Called on every BeginFrame. The banner is written exactly once.
        /// </summary>
        private static void EnsureLogAndBanner()
        {
            if (_bannerWritten) return;

            if (_logPath == null)
            {
                _logPath = Path.Combine(Environment.CurrentDirectory, "codewalker_eye_debug.log");
                //try { File.WriteAllText(_logPath, ""); } catch { }
            }

            var hdr = new StringBuilder();
            hdr.AppendLine("############################################################");
            hdr.AppendLine("# CodeWalker eye debug log");
            hdr.AppendLine("# Session started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            hdr.AppendLine("# Log file path: " + Path.GetFullPath(_logPath));
            hdr.AppendLine("# Working directory: " + Environment.CurrentDirectory);
            hdr.AppendLine("############################################################");
            hdr.AppendLine("#");
            hdr.AppendLine("# ENVIRONMENT VARIABLES READ:");
            hdr.AppendLine("#   CODEWALKER_EYE_CUTSCENE = \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_CUTSCENE") ?? "(not set)") + "\"");
            hdr.AppendLine("#   CODEWALKER_EYE_PED      = \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_PED") ?? "(not set)") + "\"");
            hdr.AppendLine("#   CODEWALKER_EYE_TMIN     = \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_TMIN") ?? "(not set)") + "\"");
            hdr.AppendLine("#   CODEWALKER_EYE_TMAX     = \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_TMAX") ?? "(not set)") + "\"");
            hdr.AppendLine("#   CODEWALKER_EYE_MAXFRAMES= \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_MAXFRAMES") ?? "(not set)") + "\"");
            hdr.AppendLine("#   CODEWALKER_EYE_DISABLE  = \"" + (Environment.GetEnvironmentVariable("CODEWALKER_EYE_DISABLE") ?? "(not set)") + "\"");
            hdr.AppendLine("#");
            hdr.AppendLine("# PARSED FILTER VALUES:");
            hdr.AppendLine("#   cutscene filter: " + (string.IsNullOrEmpty(_fCutscene) ? "(none — match all)" : "\"" + _fCutscene + "\""));
            hdr.AppendLine("#   ped filter:      " + (string.IsNullOrEmpty(_fPed) ? "(none — match all)" : "\"" + _fPed + "\""));
            hdr.AppendLine("#   time filter:     " +
                (_fTMin == float.MinValue ? "no min" : _fTMin.ToString("F3", CultureInfo.InvariantCulture) + "s") +
                " .. " +
                (_fTMax == float.MaxValue ? "no max" : _fTMax.ToString("F3", CultureInfo.InvariantCulture) + "s"));
            hdr.AppendLine("#   max frames:      " + (_maxFrames > 0 ? _maxFrames.ToString() : "unlimited"));
            hdr.AppendLine("#   disabled:        " + _disabled);
            hdr.AppendLine("#");
            hdr.AppendLine("# HOW TO USE THIS LOG:");
            hdr.AppendLine("# 1. If you see no FRAME blocks below, scroll to the DISCOVERY and REJECTIONS");
            hdr.AppendLine("#    sections at the end of this file.");
            hdr.AppendLine("# 2. DISCOVERY lists every (cutscene, ped) combo actually seen by the logger,");
            hdr.AppendLine("#    with the REAL name strings. Copy-paste these into your env vars.");
            hdr.AppendLine("# 3. REJECTIONS tells you which filter rejected frames and how many.");
            hdr.AppendLine("# 4. If DISCOVERY is empty, the logger was never called — the cutscene may");
            hdr.AppendLine("#    not have been played, or the render path doesn't reach Renderable.UpdateAnims.");
            hdr.AppendLine("#");
            hdr.AppendLine("# FRAME BLOCK FORMAT:");
            hdr.AppendLine("#   cutscene = cutscene short name");
            hdr.AppendLine("#   ped      = ped model name");
            hdr.AppendLine("#   cut_t    = cutscene time / total duration (seconds)");
            hdr.AppendLine("#   anim_t   = animation time passed to Renderable.UpdateAnims");
            hdr.AppendLine("# Quaternion = (x, y, z, w), w = scalar. |len| should be ~1.0.");
            hdr.AppendLine("#");
            hdr.AppendLine("############################################################");
            hdr.AppendLine();
            hdr.AppendLine("# >>>>> DETAILED FRAME DATA BELOW (only for frames passing all filters) <<<<<");
            hdr.AppendLine();

            //File.AppendAllText(_logPath, hdr.ToString(), Encoding.UTF8);
            _bannerWritten = true;

            // Also emit to debug output so it's visible in Visual Studio
            Debug.WriteLine("[EyeDebugLog] Log file created at: " + Path.GetFullPath(_logPath));
            Debug.WriteLine("[EyeDebugLog] Filters: cutscene=\"" + _fCutscene + "\" ped=\"" + _fPed + "\" tmin=" + _fTMin + " tmax=" + _fTMax);
        }

        /// <summary>Called once at the start of each Renderable.UpdateAnim pass.</summary>
        public static void BeginFrame(double animTime)
        {
            LoadFilters();
            if (_disabled) return;

            lock (_lock)
            {
                _totalBeginFrameCalls++;

                // ALWAYS create the log + write banner on the very first call,
                // regardless of whether the filter passes. This ensures the user
                // always gets a log file with feedback.
                EnsureLogAndBanner();

                // Track discovery: every unique (cutscene, ped) combo
                string discKey = (CutsceneName ?? "") + "|" + (PedName ?? "");
                if (!_discovered.ContainsKey(discKey))
                {
                    _discovered[discKey] = (CutsceneName ?? "", PedName ?? "", CutsceneTime);
                    WriteDiscoveryUpdate();
                }

                if (_maxFrames > 0 && _frameCounter >= _maxFrames) return;

                var (passes, reason) = CheckFilter();
                if (!passes)
                {
                    // Track rejection reason for the summary
                    if (reason == "cutscene") _rejectedByCutscene++;
                    else if (reason == "ped") _rejectedByPed++;
                    else if (reason == "time") _rejectedByTime++;
                    return;
                }

                _frameBuffer.Clear();
                _frameBuffer.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "===== FRAME {0}  cutscene={1}  ped={2}  cut_t={3:F4}s/{4:F4}s  anim_t={5:F6}s =====",
                    _frameCounter,
                    CutsceneName ?? "(none)",
                    PedName ?? "(none)",
                    CutsceneTime,
                    CutsceneDuration,
                    animTime));
            }
        }

        /// <summary>
        /// Appends the current discovery table to the log file.
        /// Called whenever a new (cutscene, ped) combo is first seen.
        /// Writes to a separate file to avoid rewriting the main log.
        /// </summary>
        private static void WriteDiscoveryUpdate()
        {
            if (_logPath == null) return;

            var discPath = Path.Combine(
                Path.GetDirectoryName(_logPath),
                "codewalker_eye_debug_DISCOVERY.txt");

            var sb = new StringBuilder();
            sb.AppendLine("CodeWalker eye debug — DISCOVERY log");
            sb.AppendLine("Updated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.AppendLine("Total BeginFrame calls so far: " + _totalBeginFrameCalls);
            sb.AppendLine();
            sb.AppendLine("Unique (cutscene, ped) combos seen:");
            sb.AppendLine("  (Copy-paste the 'ped' value into CODEWALKER_EYE_PED env var)");
            sb.AppendLine("  (Copy-paste the 'cutscene' value into CODEWALKER_EYE_CUTSCENE env var)");
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-40} {1,-40} {2,12}", "CUTSCENE", "PED", "FIRST_SEEN_s"));
            sb.AppendLine("  " + new string('-', 40) + " " + new string('-', 40) + " " + new string('-', 12));
            foreach (var kvp in _discovered)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-40} {1,-40} {2,12:F4}",
                    "\"" + (kvp.Value.cutscene ?? "") + "\"",
                    "\"" + (kvp.Value.ped ?? "") + "\"",
                    kvp.Value.firstTime));
            }
            sb.AppendLine();
            sb.AppendLine("Filter rejection counts so far:");
            sb.AppendLine("  rejected by cutscene filter: " + _rejectedByCutscene);
            sb.AppendLine("  rejected by ped filter:      " + _rejectedByPed);
            sb.AppendLine("  rejected by time filter:     " + _rejectedByTime);
            sb.AppendLine("  rejected (no eye bones):     " + _rejectedByNoEyeBones);
            sb.AppendLine("  total BeginFrame calls:      " + _totalBeginFrameCalls);
            sb.AppendLine();
            sb.AppendLine("ACTIVE FILTERS:");
            sb.AppendLine("  CODEWALKER_EYE_CUTSCENE = \"" + _fCutscene + "\"");
            sb.AppendLine("  CODEWALKER_EYE_PED      = \"" + _fPed + "\"");
            if (_fTMin != float.MinValue) sb.AppendLine("  CODEWALKER_EYE_TMIN     = " + _fTMin.ToString("F3", CultureInfo.InvariantCulture));
            if (_fTMax != float.MaxValue) sb.AppendLine("  CODEWALKER_EYE_TMAX     = " + _fTMax.ToString("F3", CultureInfo.InvariantCulture));

            // try { File.WriteAllText(discPath, sb.ToString(), Encoding.UTF8); } catch { }
        }

        public static void Log(string category, string boneName, ushort boneId, string message)
        {
            if (_disabled) return;
            lock (_lock)
            {
                if (_maxFrames > 0 && _frameCounter >= _maxFrames) return;
                if (_frameBuffer.Count == 0) return; // frame was filtered out
                _frameBuffer.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "  [{0}] bone={1} id={2} | {3}",
                    category, boneName ?? "?", boneId, message));
            }
        }

        public static void LogQuaternion(string category, string boneName, ushort boneId, string label, Quaternion q)
        {
            Log(category, boneName, boneId, string.Format(
                CultureInfo.InvariantCulture,
                "{0} = ({1:F6}, {2:F6}, {3:F6}, {4:F6}) |len|={5:F6}",
                label, q.X, q.Y, q.Z, q.W,
                (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W)));
        }

        public static void LogVector3(string category, string boneName, ushort boneId, string label, Vector3 v)
        {
            Log(category, boneName, boneId, string.Format(
                CultureInfo.InvariantCulture,
                "{0} = ({1:F6}, {2:F6}, {3:F6})",
                label, v.X, v.Y, v.Z));
        }

        public static void LogVector4(string category, string boneName, ushort boneId, string label, Vector4 v)
        {
            Log(category, boneName, boneId, string.Format(
                CultureInfo.InvariantCulture,
                "{0} = ({1:F6}, {2:F6}, {3:F6}, {4:F6})",
                label, v.X, v.Y, v.Z, v.W));
        }

        public static void LogFloat(string category, string boneName, ushort boneId, string label, float f)
        {
            Log(category, boneName, boneId, string.Format(
                CultureInfo.InvariantCulture, "{0} = {1:F6}", label, f));
        }

        /// <summary>Called at the end of each Renderable.UpdateAnim pass to flush the frame to disk.</summary>
        public static void EndFrame()
        {
            if (_disabled) return;
            lock (_lock)
            {
                if (_maxFrames > 0 && _frameCounter >= _maxFrames)
                {
                    _frameBuffer.Clear();
                    return;
                }
                if (_logPath == null)
                {
                    _frameBuffer.Clear();
                    return;
                }
                if (_frameBuffer.Count == 0)
                {
                    // Frame was filtered out — nothing to flush.
                    // Update discovery/rejection counts periodically.
                    return;
                }

                // Skip frames that have only a header (no eye bone data was logged).
                if (_frameBuffer.Count <= 1)
                {
                    _frameBuffer.Clear();
                    _rejectedByNoEyeBones++;
                    return;
                }

                _frameBuffer.Add("");
                //File.AppendAllLines(_logPath, _frameBuffer, Encoding.UTF8);
                _frameBuffer.Clear();
                _frameCounter++;
            }
        }

        public static bool IsEyeBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
