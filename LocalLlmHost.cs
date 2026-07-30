using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakRect
{
    /// <summary>
    /// Starts / stops the Local-LLM host (KoboldCpp binary) from the <c>koboldcpp</c>
    /// folder next to SpeakRect.exe. Model + mmproj come from <c>ocr.kcpps</c>
    /// (<c>model_param</c> / <c>mmproj</c>) — drop any GGUFs in that folder and
    /// point the config at them. Child is placed in a Windows Job with
    /// KILL_ON_JOB_CLOSE so it dies when SpeakRect exits or is killed.
    /// Public so smoke tests can start/wait on the host.
    /// </summary>
    public static class LocalLlmHost
    {
        private const string FolderName = "koboldcpp";
        private const string ExeName = "koboldcpp.exe";
        private const string ConfigName = "ocr.kcpps";
        private const string RuntimeConfigName = "ocr.runtime.kcpps";
        private const int DefaultPort = 5001;

        private static Process? _process;
        private static IntPtr _job = IntPtr.Zero;
        private static string? _koboldDir;
        private static int _port = DefaultPort;
        private static string _modelApiId = "koboldcpp/glmocr-Q8_0";
        private static readonly object Gate = new();

        /// <summary>Port from ocr.kcpps (after last Start). OCR client uses this.</summary>
        public static int Port => _port;

        /// <summary>
        /// OpenAI chat <c>model</c> id for the active GGUF
        /// (typically <c>koboldcpp/{stem of model_param}</c>).
        /// </summary>
        public static string ModelApiId => _modelApiId;

        /// <summary>OpenAI-compatible base URL (trailing slash).</summary>
        public static string ApiBaseUrl =>
            $"http://127.0.0.1:{Math.Clamp(_port, 1, 65535)}/v1/";

        private static string HealthUrl => ApiBaseUrl + "models";

        /// <summary>
        /// Start (or adopt) bundled Local-LLM host. Safe to call more than once.
        /// Does not block on model load (use <see cref="WaitUntilReadyAsync"/>).
        /// File log only in Debug builds; Release/publish use Debug.WriteLine only.
        /// </summary>
        public static void Start()
        {
            lock (Gate)
            {
                try
                {
                    if (IsOurProcessAlive())
                    {
                        HostLog($"Start: our process still alive pid={_process?.Id}");
                        return;
                    }

                    string? dir = ResolveKoboldDir();
                    if (dir == null)
                    {
                        HostLog("Start: koboldcpp folder not found; skipping auto-start.");
                        return;
                    }

                    _koboldDir = dir;

                    // Always learn model id + port from ocr.kcpps (even when reusing
                    // a healthy process so OCR chat requests match the loaded GGUF).
                    string templateConfig = Path.Combine(dir, ConfigName);
                    if (File.Exists(templateConfig) &&
                        TryResolveModelsFromConfig(
                            dir, templateConfig,
                            out _, out _, out string apiId, out _))
                    {
                        _modelApiId = apiId;
                    }
                    ReadPortFromTemplate(dir);

                    // If something already answers on the OCR port, keep it.
                    // (Avoid killing a healthy server / mid-load model for no gain.)
                    if (ProbeSync())
                    {
                        HostLog(
                            $"Start: API already healthy on port {_port} " +
                            $"(model id={_modelApiId}) — reusing, not restarting.");
                        return;
                    }

                    // Previous SpeakRect run may have left dead orphans if Stop never ran.
                    KillBundledKoboldProcesses(dir);

                    string exePath = Path.Combine(dir, ExeName);
                    string runtimeConfig = Path.Combine(dir, RuntimeConfigName);

                    if (!File.Exists(exePath))
                    {
                        HostLog($"Start: missing {ExeName} in {dir}");
                        return;
                    }

                    if (!File.Exists(templateConfig))
                    {
                        HostLog($"Start: missing {ConfigName} — set model_param + mmproj there.");
                        return;
                    }

                    if (!TryResolveModelsFromConfig(
                            dir, templateConfig,
                            out string modelPath, out string mmprojPath, out string modelApiId,
                            out string? resolveErr))
                    {
                        HostLog($"Start: {resolveErr}");
                        return;
                    }

                    _modelApiId = modelApiId;
                    WriteRuntimeConfig(templateConfig, runtimeConfig, modelPath, mmprojPath);
                    EnsureJob();

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"--config \"{runtimeConfig}\" --skiplauncher --quiet",
                        WorkingDirectory = dir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };

                    HostLog(
                        $"Start: model={Path.GetFileName(modelPath)} mmproj={Path.GetFileName(mmprojPath)} " +
                        $"apiId={_modelApiId} launching \"{exePath}\"");
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        HostLog("Start: Process.Start returned null.");
                        return;
                    }

                    // Tie child lifetime to SpeakRect: closing the job kills the tree.
                    // Nested-job environments can fail Assign — process still runs.
                    if (_job != IntPtr.Zero)
                    {
                        if (!AssignProcessToJobObject(_job, proc.Handle))
                        {
                            HostLog(
                                $"Start: AssignProcessToJobObject failed err={Marshal.GetLastWin32Error()} " +
                                "(process still running without job membership)");
                        }
                    }

                    _process = proc;
                    HostLog($"Start: launched pid={proc.Id} port={_port}");

                    // Quick crash detection (bad config / missing CUDA).
                    try
                    {
                        if (proc.WaitForExit(1500))
                        {
                            HostLog($"Start: process exited immediately code={proc.ExitCode}");
                            _process = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        HostLog($"Start: WaitForExit probe: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    HostLog($"Start failed: {ex}");
                }
            }
        }

        /// <summary>Synchronous health probe (short timeout).</summary>
        public static bool IsApiReady()
        {
            try { return ProbeSync(); }
            catch { return false; }
        }

        private static bool ProbeSync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = http.GetAsync(HealthUrl).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static void ReadPortFromTemplate(string dir)
        {
            try
            {
                string template = Path.Combine(dir, ConfigName);
                if (!File.Exists(template)) return;
                var root = JsonNode.Parse(File.ReadAllText(template));
                int? p = root?["port"]?.GetValue<int?>() ?? root?["port_param"]?.GetValue<int?>();
                if (p is > 0 and <= 65535)
                    _port = p.Value;
            }
            catch { /* keep current _port */ }
        }

        private static void HostLog(string message)
        {
            Debug.WriteLine("[LocalLlmHost] " + message);
#if DEBUG
            // Never write host log files in Release / publish.
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                string? dir = _koboldDir ?? ResolveKoboldDir();
                if (dir == null) return;
                File.AppendAllText(
                    Path.Combine(dir, "speakrect_kobold_host.log"),
                    line + Environment.NewLine);
            }
            catch { /* ignore log IO */ }
#endif
        }

        /// <summary>
        /// Kill the bundled Local-LLM host we own. Always safe; call on every exit path.
        /// </summary>
        public static void Stop()
        {
            lock (Gate)
            {
                try
                {
                    var proc = _process;
                    _process = null;

                    if (proc != null)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill(entireProcessTree: true);
                                proc.WaitForExit(5000);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[LocalLlmHost] Kill process failed: {ex.Message}");
                        }
                        finally
                        {
                            try { proc.Dispose(); } catch { /* ignore */ }
                        }
                    }

                    // Sweep anything still running from our kobold folder
                    // (re-spawned children, orphans, etc.).
                    string? dir = _koboldDir ?? ResolveKoboldDir();
                    if (dir != null)
                        KillBundledKoboldProcesses(dir);

                    // Closing the job also kills any remaining members (KILL_ON_JOB_CLOSE).
                    CloseJob();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LocalLlmHost] Stop failed: {ex.Message}");
                }
            }
        }

        /// <summary>Best-effort wait until /v1/models answers.</summary>
        public static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(http, ct).ConfigureAwait(false))
                    return true;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            return false;
        }

        private static bool IsOurProcessAlive()
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> ProbeAsync(HttpClient http, CancellationToken ct)
        {
            try
            {
                using var response = await http.GetAsync(HealthUrl, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kill every process whose main module is our bundled koboldcpp.exe.
        /// </summary>
        private static void KillBundledKoboldProcesses(string koboldDir)
        {
            string expectedExe = Path.GetFullPath(Path.Combine(koboldDir, ExeName));
            string expectedDir = Path.GetFullPath(koboldDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName("koboldcpp");
            }
            catch
            {
                return;
            }

            foreach (var p in candidates)
            {
                try
                {
                    using (p)
                    {
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { /* access denied / 32-bit */ }

                        bool ours = false;
                        if (!string.IsNullOrEmpty(path))
                        {
                            string full = Path.GetFullPath(path);
                            ours = full.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) ||
                                   full.StartsWith(expectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                   full.StartsWith(expectedDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            // Can't read path — if working set is only our copy, still try by name
                            // only when it lives under our dir via QueryFullProcessImageName.
                            ours = TryGetProcessImagePath(p.Id, out string? img) &&
                                   !string.IsNullOrEmpty(img) &&
                                   (img.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) ||
                                    img.StartsWith(expectedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                    img.StartsWith(expectedDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                        }

                        if (!ours)
                            continue;

                        if (!p.HasExited)
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(3000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LocalLlmHost] KillBundled pid failed: {ex.Message}");
                }
            }
        }

        private static void EnsureJob()
        {
            if (_job != IntPtr.Zero)
                return;

            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
            {
                Debug.WriteLine($"[LocalLlmHost] CreateJobObject failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    // When SpeakRect closes (or is killed), the job handle closes → children die.
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(_job, JobObjectInfoClass.JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                {
                    Debug.WriteLine($"[LocalLlmHost] SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
                    CloseJob();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        private static void CloseJob()
        {
            if (_job == IntPtr.Zero)
                return;
            try { CloseHandle(_job); } catch { /* ignore */ }
            _job = IntPtr.Zero;
        }

        private static string? ResolveKoboldDir()
        {
            // Search next to the exe, then walk up so dev builds under
            // bin\x64\Release\… still find <repo>\koboldcpp next to the .sln/.csproj.
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
                roots.Add(AppContext.BaseDirectory);

            string? processDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(processDir))
                roots.Add(processDir);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string start in roots)
            {
                string? dir = start;
                // Publish: <app>\koboldcpp  |  Dev: walk up bin\… → repo root
                for (int depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(dir); depth++)
                {
                    string full = Path.GetFullPath(dir);
                    if (!seen.Add(full))
                    {
                        dir = Directory.GetParent(full)?.FullName;
                        continue;
                    }

                    string? hit = TryKoboldDir(Path.Combine(full, FolderName));
                    if (hit != null)
                        return hit;

                    dir = Directory.GetParent(full)?.FullName;
                }
            }

            return null;
        }

        private static string? TryKoboldDir(string dir)
        {
            if (!Directory.Exists(dir))
                return null;
            if (!File.Exists(Path.Combine(dir, ExeName)))
                return null;
            return Path.GetFullPath(dir);
        }

        /// <summary>
        /// Read <c>model_param</c> / <c>mmproj</c> (and optional <c>model</c>) from
        /// ocr.kcpps and resolve to absolute paths under the kobold folder (or as-is
        /// if the config already has an absolute path that exists).
        /// </summary>
        private static bool TryResolveModelsFromConfig(
            string koboldDir,
            string templatePath,
            out string modelPath,
            out string mmprojPath,
            out string modelApiId,
            out string? error)
        {
            modelPath = "";
            mmprojPath = "";
            modelApiId = _modelApiId;
            error = null;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(templatePath));
            }
            catch (Exception ex)
            {
                error = $"could not parse {ConfigName}: {ex.Message}";
                return false;
            }

            if (root == null)
            {
                error = $"{ConfigName} is empty.";
                return false;
            }

            string? modelRaw = ReadConfigPathField(root, "model_param")
                ?? ReadConfigPathField(root, "model");
            string? mmprojRaw = ReadConfigPathField(root, "mmproj");

            if (string.IsNullOrWhiteSpace(modelRaw))
            {
                error =
                    $"{ConfigName} has no model_param (or model). " +
                    "Set model_param to a .gguf file name in the koboldcpp folder.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(mmprojRaw))
            {
                error =
                    $"{ConfigName} has no mmproj. " +
                    "Set mmproj to the vision projector .gguf file name.";
                return false;
            }

            if (!TryResolveGgufPath(koboldDir, modelRaw, out modelPath))
            {
                error =
                    $"model not found: \"{modelRaw}\" " +
                    $"(looked in {koboldDir} and as absolute path). " +
                    "Drop the .gguf next to koboldcpp.exe and set model_param in ocr.kcpps.";
                return false;
            }

            if (!TryResolveGgufPath(koboldDir, mmprojRaw, out mmprojPath))
            {
                error =
                    $"mmproj not found: \"{mmprojRaw}\" " +
                    $"(looked in {koboldDir} and as absolute path). " +
                    "Drop the projector .gguf next to koboldcpp.exe and set mmproj in ocr.kcpps.";
                return false;
            }

            // Kobold OpenAI id is typically koboldcpp/{file stem}.
            string stem = Path.GetFileNameWithoutExtension(modelPath);
            modelApiId = string.IsNullOrWhiteSpace(stem)
                ? "koboldcpp"
                : "koboldcpp/" + stem;
            return true;
        }

        /// <summary>
        /// Read a path-like string field; for arrays take the first non-empty string entry.
        /// </summary>
        private static string? ReadConfigPathField(JsonNode root, string key)
        {
            JsonNode? node = root[key];
            if (node == null)
                return null;

            if (node is JsonValue)
            {
                string? s = node.GetValue<string?>();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }

            if (node is JsonArray arr)
            {
                foreach (JsonNode? item in arr)
                {
                    if (item == null) continue;
                    string? s = item.GetValue<string?>();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve a config path: absolute if it exists, else relative to koboldDir
        /// (basename only is fine — e.g. <c>my-ocr.gguf</c>).
        /// </summary>
        private static bool TryResolveGgufPath(string koboldDir, string raw, out string fullPath)
        {
            fullPath = "";
            string t = raw.Trim().Trim('"');
            if (t.Length == 0)
                return false;

            // Normalize slashes for Path APIs
            t = t.Replace('/', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(t) && File.Exists(t))
            {
                fullPath = Path.GetFullPath(t);
                return true;
            }

            // Relative to kobold folder (full relative path or bare filename)
            string candidate = Path.GetFullPath(Path.Combine(koboldDir, t));
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }

            // Bare filename even if config had a stale absolute path from another machine
            string name = Path.GetFileName(t);
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidate = Path.GetFullPath(Path.Combine(koboldDir, name));
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Load ocr.kcpps as-is (CUDA, layers, context, etc.), then only fix:
        /// model/mmproj absolute paths for this install, and headless launch flags.
        /// Does not change which model the user selected in the template.
        /// </summary>
        private static void WriteRuntimeConfig(
            string templatePath,
            string runtimePath,
            string modelPath,
            string mmprojPath)
        {
            JsonNode root;
            if (File.Exists(templatePath))
            {
                string text = File.ReadAllText(templatePath);
                root = JsonNode.Parse(text) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            string modelNorm = modelPath.Replace('\\', '/');
            string mmprojNorm = mmprojPath.Replace('\\', '/');

            // Absolute paths so Kobold finds the GGUFs regardless of CWD quirks.
            root["model_param"] = modelNorm;
            root["mmproj"] = mmprojNorm;

            // Port from ocr.kcpps (OCR client follows this).
            int port = DefaultPort;
            try
            {
                int? p = root["port"]?.GetValue<int?>() ?? root["port_param"]?.GetValue<int?>();
                if (p is > 0 and <= 65535)
                    port = p.Value;
            }
            catch { /* keep default */ }
            root["port"] = port;
            root["port_param"] = port;
            _port = port;

            // Launch always headless under SpeakRect (settings in kcpps otherwise untouched).
            root["showgui"] = false;
            root["launch"] = false;
            root["quiet"] = true;
            root["foreground"] = false;
            root["cli"] = false;

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(runtimePath, root.ToJsonString(options));
        }

        // -----------------------------------------------------------------------
        // Win32: Job Object + process image path
        // -----------------------------------------------------------------------

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        private enum JobObjectInfoClass
        {
            JobObjectExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            JobObjectInfoClass jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            System.Text.StringBuilder lpExeName,
            ref uint lpdwSize);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static bool TryGetProcessImagePath(int pid, out string? path)
        {
            path = null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return false;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size))
                    return false;
                path = Path.GetFullPath(sb.ToString());
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
