// T3 Code (Windows Nightly) — Groq Whisper On-Launch Patcher & Launcher
// Single-file C# (.NET Framework 4.x, built-in Windows csc.exe, zero dependencies).
//
// Compile on any Windows 10/11 PC:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /out:T3Code-Groq.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll T3CodeGroq.cs
//
// Usage:
//   T3Code-Groq.exe                       Patch if server.asar (or app.asar icon/name) changed, then launch T3 Code
//   T3Code-Groq.exe --help                Show CLI options and usage examples
//   T3Code-Groq.exe --dry-run             Test patch on a temp copy (never touches install)
//   T3Code-Groq.exe --patch-only          Patch if needed without launching
//   T3Code-Groq.exe --restore             Restore server.asar.bak (and app.asar.bak if present)
//   T3Code-Groq.exe --url <endpoint>      Custom transcription endpoint URL (or base URL without /audio/transcriptions)
//   T3Code-Groq.exe --model whisper-large-v3
//   T3Code-Groq.exe --skip-asar           Skip server.asar transcription patching (icon/name branding-only mode)
//   T3Code-Groq.exe --icon                         Pick an icon (.ico / .exe / .dll / .lnk) via Windows file dialog
//   T3Code-Groq.exe --icon "C:\path\to\Code.exe"   Patch T3 Code's Alt+Tab / window icon (accepts .ico, .exe, .dll)
//   T3Code-Groq.exe --name "Visual Studio Code"    Patch T3 Code's shortcut name & Alt+Tab window title
//   T3Code-Groq.exe --create-shortcut              Default T3 Code icon + merged taskbar button
//   T3Code-Groq.exe --create-shortcut --icon --name "Visual Studio Code"

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("T3Code-Groq")]
[assembly: AssemblyDescription("On-launch Groq Whisper patcher and launcher for T3 Code")]
[assembly: AssemblyProduct("T3Code-Groq")]
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]

static class Program
{
    const string DefaultGroqUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    const string DefaultModel = "whisper-large-v3";
    const string DefaultAumid = "com.t3tools.t3code";

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    static int Main(string[] args)
    {
        bool showHelp = HasAnyFlag(args, "--help", "-h", "/?");
        bool dryRun = HasFlag(args, "--dry-run");
        bool patchOnly = HasFlag(args, "--patch-only");
        bool restore = HasFlag(args, "--restore");
        bool createShortcut = HasFlag(args, "--create-shortcut");
        bool skipAsar = HasAnyFlag(args, "--skip-asar", "--no-asar");
        bool skipIcon = HasFlag(args, "--skip-icon");
        bool skipName = HasFlag(args, "--skip-name");
        bool cliMode = showHelp || dryRun || patchOnly || restore || createShortcut;
        bool attachedConsole = false;
        if (cliMode) attachedConsole = AttachConsole(-1);

        if (showHelp)
        {
            string helpText = BuildHelpText();
            if (attachedConsole || Console.IsOutputRedirected)
                Console.WriteLine(helpText);
            else
                MessageBox.Show(helpText, "T3Code-Groq Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        string model = GetOpt(args, "--model", DefaultModel);
        string installDir = GetOpt(
            args,
            "--install-dir",
            Environment.GetEnvironmentVariable("T3CODE_INSTALL_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "t3code"));

        string exePath = FindExe(installDir);
        string asarPath = Path.Combine(installDir, "resources", "server.asar");
        string backupPath = Path.Combine(installDir, "resources", "server.asar.bak");
        string appAsarPath = Path.Combine(installDir, "resources", "app.asar");
        string appBackupPath = Path.Combine(installDir, "resources", "app.asar.bak");
        string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "T3Code-Groq");
        string statePath = Path.Combine(stateDir, "state.txt");
        string iconConfigPath = Path.Combine(stateDir, "icon.txt");
        string nameConfigPath = Path.Combine(stateDir, "name.txt");
        string logPath = Path.Combine(stateDir, "patcher.log");

        bool iconFlagPresent;
        string iconOpt;
        TryGetOptValue(args, "--icon", out iconFlagPresent, out iconOpt);
        string nameOpt = GetOpt(args, "--name", null);

        bool urlFlagPresent;
        string urlOpt;
        TryGetOptValueAny(args, out urlFlagPresent, out urlOpt, "--url", "--groq-url");

        try
        {
            if (urlFlagPresent && string.IsNullOrWhiteSpace(urlOpt))
                throw new ArgumentException("Missing value for --url / --groq-url.");

            string rawUrl = !string.IsNullOrWhiteSpace(urlOpt)
                ? urlOpt
                : Environment.GetEnvironmentVariable("T3CODE_GROQ_URL");
            string targetUrl = NormalizeTranscriptionUrl(rawUrl);

            if (skipIcon)
            {
                iconFlagPresent = false;
                iconOpt = null;
            }
            if (skipName)
            {
                nameOpt = null;
            }

            if (iconFlagPresent && string.IsNullOrEmpty(iconOpt))
            {
                iconOpt = PickIconFile();
                if (string.IsNullOrEmpty(iconOpt))
                {
                    Log(logPath, cliMode, "Icon selection cancelled.");
                    return 0;
                }
            }

            if (createShortcut)
            {
                string selfExe = Process.GetCurrentProcess().MainModule.FileName;
                string defaultAppName = Path.GetFileNameWithoutExtension(exePath);
                if (string.IsNullOrEmpty(defaultAppName)) defaultAppName = "T3 Code";
                string defaultLnkName = (!string.IsNullOrEmpty(nameOpt) ? SanitizeFileName(nameOpt) : SanitizeFileName(defaultAppName)) + ".lnk";
                string shortcutPath = GetOpt(
                    args,
                    "--shortcut",
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        defaultLnkName));
                string iconPath = iconOpt ?? (File.Exists(exePath) ? exePath : selfExe);
                string aumid = ResolveAumid(stateDir, GetOpt(args, "--aumid", null));
                SaveAumid(stateDir, aumid);
                Guid? toastClsid = DetectToastActivatorClsid(stateDir, exePath);
                SaveToastActivatorClsid(stateDir, toastClsid);
                bool customWindowIcon = !string.IsNullOrEmpty(iconOpt)
                    && !string.Equals(Path.GetFullPath(iconOpt), Path.GetFullPath(selfExe), StringComparison.OrdinalIgnoreCase)
                    && (!File.Exists(exePath) || !string.Equals(Path.GetFullPath(iconOpt), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase));

                string lnkArgs = BuildShortcutArgs(
                    targetUrl,
                    model,
                    skipAsar,
                    skipIcon,
                    skipName,
                    customWindowIcon ? iconOpt : null,
                    !string.IsNullOrEmpty(nameOpt) ? nameOpt : null);
                string desc = !string.IsNullOrEmpty(nameOpt) ? nameOpt : defaultAppName;

                CreateMergedShortcut(shortcutPath, selfExe, lnkArgs, iconPath, aumid, toastClsid, desc);
                if (!skipIcon)
                {
                    if (customWindowIcon)
                    {
                        Directory.CreateDirectory(stateDir);
                        File.WriteAllText(iconConfigPath, iconOpt);
                    }
                    else if (File.Exists(iconConfigPath))
                    {
                        File.Delete(iconConfigPath);
                    }
                }
                if (!skipName)
                {
                    if (!string.IsNullOrEmpty(nameOpt))
                    {
                        Directory.CreateDirectory(stateDir);
                        File.WriteAllText(nameConfigPath, nameOpt);
                    }
                    else if (File.Exists(nameConfigPath))
                    {
                        File.Delete(nameConfigPath);
                    }
                }
                SyncStartMenuShortcut(
                    stateDir,
                    selfExe,
                    exePath,
                    customWindowIcon ? iconOpt : null,
                    !string.IsNullOrEmpty(nameOpt) ? nameOpt : null,
                    targetUrl,
                    model,
                    skipAsar,
                    skipIcon,
                    skipName,
                    aumid,
                    logPath,
                    cliMode);
                Log(logPath, cliMode, "Created shortcut: " + shortcutPath + " (AppUserModelID=" + aumid + ")");
                return 0;
            }

            if (restore)
            {
                bool restoredAny = false;
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, asarPath, true);
                    Log(logPath, cliMode, "Restored original server.asar from server.asar.bak.");
                    restoredAny = true;
                }
                if (File.Exists(appBackupPath))
                {
                    File.Copy(appBackupPath, appAsarPath, true);
                    Log(logPath, cliMode, "Restored original app.asar from app.asar.bak.");
                    restoredAny = true;
                }
                if (RestoreStartMenuShortcut(stateDir, logPath, cliMode))
                    restoredAny = true;
                if (!restoredAny)
                    throw new FileNotFoundException("No backups found to restore (checked server.asar.bak, app.asar.bak, and startmenu.lnk.bak).");
                if (File.Exists(statePath)) File.Delete(statePath);
                if (File.Exists(iconConfigPath)) File.Delete(iconConfigPath);
                if (File.Exists(nameConfigPath)) File.Delete(nameConfigPath);
                return 0;
            }

            if (!skipAsar && !File.Exists(asarPath))
                throw new FileNotFoundException("server.asar not found at: " + asarPath);

            string activeIconPath = null;
            if (!skipIcon)
            {
                activeIconPath = iconOpt;
                if (string.IsNullOrEmpty(activeIconPath) && File.Exists(iconConfigPath))
                {
                    string saved = File.ReadAllText(iconConfigPath).Trim();
                    if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
                        activeIconPath = saved;
                }
            }

            string activeName = null;
            if (!skipName)
            {
                activeName = nameOpt;
                if (string.IsNullOrEmpty(activeName) && File.Exists(nameConfigPath))
                {
                    string saved = File.ReadAllText(nameConfigPath).Trim();
                    if (!string.IsNullOrEmpty(saved))
                        activeName = saved;
                }
            }

            bool shouldPatchAppAsar = File.Exists(appAsarPath)
                && ((!skipIcon && (!string.IsNullOrEmpty(activeIconPath) || File.Exists(appBackupPath)))
                    || (!skipName && (!string.IsNullOrEmpty(activeName) || File.Exists(appBackupPath))));

            if (dryRun)
            {
                if (skipAsar)
                {
                    Log(logPath, cliMode, "DRY RUN: Skipping server.asar transcription patching (--skip-asar).");
                }
                else
                {
                    PatchAsar(asarPath, backupPath, targetUrl, model, true, logPath, cliMode);
                }
                if (shouldPatchAppAsar)
                {
                    byte[] icoBytes = !string.IsNullOrEmpty(activeIconPath) ? LoadIconBytes(activeIconPath) : null;
                    PatchAppAsar(appAsarPath, appBackupPath, icoBytes, activeName, skipIcon, skipName, true, logPath, cliMode);
                }
                return 0;
            }

            string activeAumid = ResolveAumid(stateDir, GetOpt(args, "--aumid", null));

            // Fast path (<20ms): unchanged size + LastWriteTimeUtc + url + model + icon + name + aumid + Start Menu => launch immediately
            string expectedState = BuildStateKey(asarPath, targetUrl, model, skipAsar, appAsarPath, activeIconPath, activeName, skipIcon, skipName, activeAumid, stateDir);
            if (File.Exists(statePath) && File.ReadAllText(statePath).Trim() == expectedState)
            {
                if (!patchOnly) Launch(exePath, activeAumid);
                return 0;
            }

            if (skipAsar)
            {
                Log(logPath, cliMode, "Skipping server.asar transcription patching (--skip-asar).");
            }
            else
            {
                PatchAsar(asarPath, backupPath, targetUrl, model, false, logPath, cliMode);
            }

            if (shouldPatchAppAsar)
            {
                byte[] icoBytes = !string.IsNullOrEmpty(activeIconPath) ? LoadIconBytes(activeIconPath) : null;
                PatchAppAsar(appAsarPath, appBackupPath, icoBytes, activeName, skipIcon, skipName, false, logPath, cliMode);
            }

            string currentSelfExe = Process.GetCurrentProcess().MainModule.FileName;
            SyncStartMenuShortcut(
                stateDir,
                currentSelfExe,
                exePath,
                activeIconPath,
                activeName,
                targetUrl,
                model,
                skipAsar,
                skipIcon,
                skipName,
                activeAumid,
                logPath,
                cliMode);

            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                statePath,
                BuildStateKey(asarPath, targetUrl, model, skipAsar, appAsarPath, activeIconPath, activeName, skipIcon, skipName, activeAumid, stateDir));

            if (!patchOnly) Launch(exePath, activeAumid);
            return 0;
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            if ((ex is IOException && !(ex is FileNotFoundException)) || ex is UnauthorizedAccessException)
                msg += "\n\nClose T3 Code completely before patching.";
            Log(logPath, cliMode, "ERROR: " + msg);
            if (!cliMode)
                MessageBox.Show(msg, "T3 Code (Groq)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    static string BuildHelpText()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "T3Code-Groq - On-launch Groq Whisper patcher & branding launcher for T3 Code (Windows)",
            "",
            "Usage:",
            "  T3Code-Groq.exe [options]",
            "",
            "Options:",
            "  --url, --groq-url <url>  Custom transcription endpoint URL (default: " + DefaultGroqUrl + ")",
            "                           Also reads T3CODE_GROQ_URL. Trailing slashes are trimmed and",
            "                           /audio/transcriptions is appended automatically if omitted.",
            "  --model <id>             Groq Whisper model (default: " + DefaultModel + ")",
            "  --skip-asar, --no-asar   Skip server.asar transcription patching (icon/name branding-only mode)",
            "  --skip-icon              Skip custom icon patching and shortcut icon override",
            "  --skip-name              Skip custom window title and shortcut name override",
            "  --icon [path]            Override window, Alt+Tab, Taskbar, and Start icon (.ico, .exe, .dll).",
            "                           Omit path to open a Windows file picker.",
            "  --name \"<title>\"         Override shortcut name, Start Menu name, and Alt+Tab / window title",
            "  --create-shortcut        Create a merged Desktop shortcut and sync the Start Menu shortcut",
            "  --shortcut <path>        Custom .lnk output path for --create-shortcut",
            "  --aumid <id>             Override AppUserModelID for shortcut taskbar merging",
            "  --dry-run                Verify patching and ASAR integrity on a temp copy without modifying install",
            "  --patch-only             Patch if needed without launching T3 Code",
            "  --restore                Restore original server.asar, app.asar, and Start Menu shortcut from backups",
            "  --install-dir <dir>      Custom T3 Code install directory (default: %LOCALAPPDATA%\\Programs\\t3code",
            "                           or T3CODE_INSTALL_DIR)",
            "  -h, --help               Show this help message",
            "",
            "Examples:",
            "  Standard run (default official Groq URL + optional branding):",
            "    T3Code-Groq.exe --create-shortcut --icon --name \"My Editor\"",
            "",
            "  Custom proxy URL run (full URL or base URL):",
            "    T3Code-Groq.exe --url https://proxy.example.com/openai/v1/audio/transcriptions",
            "    T3Code-Groq.exe --url https://proxy.example.com/openai/v1",
            "",
            "  Branding-only run (skip server.asar transcription patch, patch icon/name only):",
            "    T3Code-Groq.exe --skip-asar --icon \"C:\\path\\to\\icon.ico\" --name \"My Editor\""
        });
    }

    static string NormalizeTranscriptionUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return DefaultGroqUrl;

        string trimmed = rawUrl.Trim();
        int qIdx = trimmed.IndexOf('?');
        string basePart = (qIdx >= 0 ? trimmed.Substring(0, qIdx) : trimmed).TrimEnd('/');
        string queryPart = qIdx >= 0 ? trimmed.Substring(qIdx) : "";

        bool isHttps = basePart.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool isHttp = basePart.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        int schemeLen = isHttps ? 8 : (isHttp ? 7 : 0);
        if (schemeLen == 0 || basePart.Length <= schemeLen)
            throw new ArgumentException("Invalid transcription URL (must start with http:// or https://): " + rawUrl);

        if (!basePart.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
            basePart += "/audio/transcriptions";

        return basePart + queryPart;
    }

    static string BuildShortcutArgs(
        string targetUrl,
        string model,
        bool skipAsar,
        bool skipIcon,
        bool skipName,
        string iconPath,
        string customName)
    {
        var argParts = new List<string>();
        if (skipAsar)
        {
            argParts.Add("--skip-asar");
        }
        else
        {
            if (!string.IsNullOrEmpty(targetUrl) && !string.Equals(targetUrl, DefaultGroqUrl, StringComparison.OrdinalIgnoreCase))
                argParts.Add("--url \"" + targetUrl.Replace("\"", "\\\"") + "\"");
            if (!string.IsNullOrEmpty(model) && !string.Equals(model, DefaultModel, StringComparison.Ordinal))
                argParts.Add("--model \"" + model.Replace("\"", "\\\"") + "\"");
        }
        if (skipIcon)
            argParts.Add("--skip-icon");
        else if (!string.IsNullOrEmpty(iconPath))
            argParts.Add("--icon \"" + iconPath + "\"");
        if (skipName)
            argParts.Add("--skip-name");
        else if (!string.IsNullOrEmpty(customName))
            argParts.Add("--name \"" + customName.Replace("\"", "\\\"") + "\"");
        return argParts.Count > 0 ? string.Join(" ", argParts.ToArray()) : null;
    }

    static bool HasAnyFlag(string[] args, params string[] flags)
    {
        for (int i = 0; i < flags.Length; i++)
            if (HasFlag(args, flags[i])) return true;
        return false;
    }

    static void TryGetOptValueAny(string[] args, out bool present, out string value, params string[] names)
    {
        present = false;
        value = null;
        for (int n = 0; n < names.Length; n++)
        {
            bool p;
            string v;
            TryGetOptValue(args, names[n], out p, out v);
            if (p)
            {
                present = true;
                value = v;
                return;
            }
        }
    }

    static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string GetOpt(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return fallback;
    }

    static void TryGetOptValue(string[] args, string name, out bool present, out string value)
    {
        present = false;
        value = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                present = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    value = args[i + 1];
                return;
            }
        }
    }

    static string PickIconFile()
    {
        Application.EnableVisualStyles();
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "Select an icon (.ico) or app (.exe / .dll)";
            dlg.Filter = "Icons & Apps (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|All files (*.*)|*.*";
            dlg.CheckFileExists = true;
            dlg.DereferenceLinks = true;
            return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
        }
    }

    static string FindExe(string dir)
    {
        if (Directory.Exists(dir))
        {
            string fallback = null;
            foreach (string file in Directory.GetFiles(dir, "*.exe"))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf("T3 Code", StringComparison.OrdinalIgnoreCase) >= 0) return file;
                if (fallback == null) fallback = file;
            }
            if (fallback != null) return fallback;
        }
        return Path.Combine(dir, "T3 Code (Nightly).exe");
    }

    static void Launch(string exePath, string aumid)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("T3 Code executable not found at: " + exePath);
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = false
        };
        if (!string.IsNullOrEmpty(aumid))
            psi.EnvironmentVariables["T3CODE_DESKTOP_APP_USER_MODEL_ID"] = aumid;
        Process.Start(psi);
    }

    static void Log(string logPath, bool toConsole, string message)
    {
        string line = "[" + DateTime.UtcNow.ToString("o") + "] " + message;
        if (toConsole) Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
    }

    static JavaScriptSerializer NewJson()
    {
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
    }

    static Dictionary<string, object> ReadAsarHeader(Stream s, out long headerSize)
    {
        byte[] hdr = new byte[16];
        ReadExact(s, hdr, 0, 16);
        headerSize = BitConverter.ToUInt32(hdr, 4) + 8L;
        int jsonLen = (int)BitConverter.ToUInt32(hdr, 12);
        byte[] jsonBytes = new byte[jsonLen];
        ReadExact(s, jsonBytes, 0, jsonLen);
        string json = Encoding.UTF8.GetString(jsonBytes);
        return NewJson().Deserialize<Dictionary<string, object>>(json);
    }

    static byte[] EncodeAsarHeader(Dictionary<string, object> tree)
    {
        string json = NewJson().Serialize(tree);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int pad = (4 - (jsonBytes.Length % 4)) % 4;
        int alignedLen = jsonBytes.Length + pad;
        byte[] outBuf = new byte[16 + alignedLen];
        Buffer.BlockCopy(BitConverter.GetBytes((uint)4), 0, outBuf, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)(alignedLen + 8)), 0, outBuf, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)(alignedLen + 4)), 0, outBuf, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)jsonBytes.Length), 0, outBuf, 12, 4);
        Buffer.BlockCopy(jsonBytes, 0, outBuf, 16, jsonBytes.Length);
        return outBuf;
    }

    static Dictionary<string, object> GetFilesDict(Dictionary<string, object> node, params string[] segments)
    {
        Dictionary<string, object> cur = node;
        foreach (string seg in segments)
        {
            object filesObj;
            if (!cur.TryGetValue("files", out filesObj)) return null;
            var files = filesObj as Dictionary<string, object>;
            if (files == null) return null;
            object nextObj;
            if (!files.TryGetValue(seg, out nextObj)) return null;
            cur = nextObj as Dictionary<string, object>;
            if (cur == null) return null;
        }
        object finalFiles;
        return cur.TryGetValue("files", out finalFiles) ? finalFiles as Dictionary<string, object> : null;
    }

    static string PatchCloudSpeechSource(string text, string targetUrl, string model, out bool changed)
    {
        var patchedEndpointRe = new Regex(
            @"(openai:\s*\{\s*url:\s*"")([^""]+)("",\s*model:\s*"")([^""]+)(""\s*\})");
        bool alreadyGroqPatched = text.Contains("const __groqWords =")
            || (!text.Contains("api.openai.com/v1/audio/transcriptions") && patchedEndpointRe.IsMatch(text));

        // If already patched for Groq (official or custom proxy URL), update URL and/or model if needed
        if (alreadyGroqPatched)
        {
            Match m = patchedEndpointRe.Match(text);
            if (!m.Success)
                throw new Exception("Patch failed: could not match existing Groq endpoint/model block.");
            if (m.Groups[2].Value == targetUrl && m.Groups[4].Value == model)
            {
                changed = false;
                return text;
            }
            changed = true;
            return patchedEndpointRe.Replace(
                text,
                "$1" + targetUrl.Replace("$", "$$") + "$3" + model.Replace("$", "$$") + "$5",
                1);
        }

        string patched = text;

        // 1. Endpoint URL + model
        var endpointRe = new Regex(
            @"(openai:\s*\{\s*url:\s*)""https://api\.openai\.com/v1/audio/transcriptions""(,\s*model:\s*)""gpt-transcribe""(\s*\})");
        if (!endpointRe.IsMatch(patched))
            throw new Exception("Patch failed: could not match OpenAI endpoint/model block.");
        patched = endpointRe.Replace(
            patched,
            "$1\"" + targetUrl.Replace("$", "$$") + "\"$2\"" + model.Replace("$", "$$") + "\"$3",
            1);

        // 2. Language field (`languages[]` -> `language`)
        var langRe = new Regex(
            @"form\.set\(provider === ""openai"" \? ""languages\[\]"" : ""language"", language\)");
        if (!langRe.IsMatch(patched))
            throw new Exception("Patch failed: could not match language form field in audioBody().");
        patched = langRe.Replace(patched, "form.set(\"language\", language)", 1);

        // 3. Custom vocabulary (`keywords[]` -> `prompt` for Groq; preserve `keyterm` for xAI)
        var kwRe = new Regex(
            @"for \(const term of (speechRecognitionWords\([^\n]+\))\) \{\s*if \(provider === ""openai"" && /\[<>\\r\\n\]/\.test\(term\)\) continue;\s*form\.append\(provider === ""openai"" \? ""keywords\[\]"" : ""keyterm"", term\);\s*\}");
        if (!kwRe.IsMatch(patched))
            throw new Exception("Patch failed: could not match keywords[] vocabulary loop in audioBody().");
        patched = kwRe.Replace(
            patched,
            "const __groqWords = $1;\n"
                + "\tif (provider === \"openai\") {\n"
                + "\t\tconst __prompt = __groqWords.join(\", \").slice(0, 800);\n"
                + "\t\tif (__prompt) form.set(\"prompt\", __prompt);\n"
                + "\t} else {\n"
                + "\t\tfor (const term of __groqWords) form.append(\"keyterm\", term);\n"
                + "\t}",
            1);

        changed = true;
        return patched;
    }

    static Dictionary<string, object> ComputeIntegrity(byte[] data, int blockSize)
    {
        using (var sha = SHA256.Create())
        {
            string fullHash = ToHex(sha.ComputeHash(data));
            var blocks = new List<string>();
            for (int off = 0; off < data.Length; off += blockSize)
            {
                int len = Math.Min(blockSize, data.Length - off);
                blocks.Add(ToHex(sha.ComputeHash(data, off, len)));
            }
            return new Dictionary<string, object>
            {
                { "algorithm", "SHA256" },
                { "hash", fullHash },
                { "blockSize", blockSize },
                { "blocks", blocks.ToArray() }
            };
        }
    }

    static void ShiftOffsets(Dictionary<string, object> node, long afterOffset, long delta)
    {
        object filesObj;
        if (!node.TryGetValue("files", out filesObj)) return;
        var files = filesObj as Dictionary<string, object>;
        if (files == null) return;

        foreach (var kv in files)
        {
            var entry = kv.Value as Dictionary<string, object>;
            if (entry == null) continue;
            if (entry.ContainsKey("files"))
            {
                ShiftOffsets(entry, afterOffset, delta);
            }
            else if (!entry.ContainsKey("unpacked") && entry.ContainsKey("offset"))
            {
                long off = long.Parse(entry["offset"].ToString());
                if (off > afterOffset)
                    entry["offset"] = (off + delta).ToString();
            }
        }
    }

    static void PatchAsar(string asarPath, string backupPath, string targetUrl, string model, bool dryRun, string logPath, bool cliMode)
    {
        string outPath = dryRun
            ? Path.Combine(Path.GetTempPath(), "server-groq-dryrun-" + DateTime.UtcNow.Ticks + ".asar")
            : asarPath + ".tmp";

        string targetName = null;
        int oldSize = 0;
        int newSize = 0;
        bool wasAlreadyPatched = false;

        using (var src = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long headerSize;
            var tree = ReadAsarHeader(src, out headerSize);
            var dist = GetFilesDict(tree, "apps", "server", "dist");
            if (dist == null) throw new Exception("Could not find apps/server/dist in server.asar.");

            Dictionary<string, object> targetEntry = null;
            string targetText = null;

            foreach (var kv in dist)
            {
                var entry = kv.Value as Dictionary<string, object>;
                if (entry == null || entry.ContainsKey("unpacked") || entry.ContainsKey("files")) continue;
                if (!(kv.Key.EndsWith(".mjs") || kv.Key.EndsWith(".js") || kv.Key.EndsWith(".cjs"))) continue;

                long off = long.Parse(entry["offset"].ToString());
                int sz = Convert.ToInt32(entry["size"]);
                byte[] buf = new byte[sz];
                src.Seek(headerSize + off, SeekOrigin.Begin);
                ReadExact(src, buf, 0, sz);
                string txt = Encoding.UTF8.GetString(buf);
                if (txt.Contains("api.openai.com/v1/audio/transcriptions")
                    || txt.Contains(targetUrl)
                    || txt.Contains(DefaultGroqUrl)
                    || txt.Contains("const __groqWords ="))
                {
                    targetName = kv.Key;
                    targetEntry = entry;
                    targetText = txt;
                    break;
                }
            }

            if (targetEntry == null)
                throw new Exception("Could not locate CloudSpeech bundle inside apps/server/dist.");

            wasAlreadyPatched = !targetText.Contains("api.openai.com/v1/audio/transcriptions");
            bool changed;
            string patchedText = PatchCloudSpeechSource(targetText, targetUrl, model, out changed);
            if (!changed)
            {
                Log(logPath, cliMode, "server.asar (" + targetName + ") is already patched for " + targetUrl + " (" + model + ").");
                return;
            }

            long oldOffset = long.Parse(targetEntry["offset"].ToString());
            oldSize = Convert.ToInt32(targetEntry["size"]);
            byte[] newBytes = Encoding.UTF8.GetBytes(patchedText);
            newSize = newBytes.Length;
            long delta = newSize - oldSize;

            targetEntry["size"] = newSize;
            if (targetEntry.ContainsKey("integrity"))
            {
                var oldInt = targetEntry["integrity"] as Dictionary<string, object>;
                int blockSize = (oldInt != null && oldInt.ContainsKey("blockSize"))
                    ? Convert.ToInt32(oldInt["blockSize"])
                    : 4 * 1024 * 1024;
                targetEntry["integrity"] = ComputeIntegrity(newBytes, blockSize);
            }
            ShiftOffsets(tree, oldOffset, delta);

            byte[] newHeader = EncodeAsarHeader(tree);

            using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                dst.Write(newHeader, 0, newHeader.Length);
                CopyRange(src, dst, headerSize, oldOffset);
                dst.Write(newBytes, 0, newBytes.Length);
                long tailStart = headerSize + oldOffset + oldSize;
                CopyRange(src, dst, tailStart, src.Length - tailStart);
            }
        }

        // Verify rewritten ASAR integrity before swapping
        using (var ver = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long vHeader;
            var vTree = ReadAsarHeader(ver, out vHeader);
            var vDist = GetFilesDict(vTree, "apps", "server", "dist");
            var vEntry = vDist[targetName] as Dictionary<string, object>;
            long vOff = long.Parse(vEntry["offset"].ToString());
            int vSz = Convert.ToInt32(vEntry["size"]);
            byte[] vBuf = new byte[vSz];
            ver.Seek(vHeader + vOff, SeekOrigin.Begin);
            ReadExact(ver, vBuf, 0, vSz);

            var vInt = vEntry["integrity"] as Dictionary<string, object>;
            if (vInt != null)
            {
                using (var sha = SHA256.Create())
                {
                    if (ToHex(sha.ComputeHash(vBuf)) != vInt["hash"].ToString())
                        throw new Exception("Verification failed: SHA-256 mismatch in rewritten ASAR.");
                }
            }
            if (!Encoding.UTF8.GetString(vBuf).Contains(targetUrl))
                throw new Exception("Verification failed: transcription URL missing in rewritten ASAR.");
        }

        if (dryRun)
        {
            File.Delete(outPath);
            Log(logPath, cliMode,
                "DRY RUN PASSED: " + targetName + " patched -> " + targetUrl + " (" + model + ", " + oldSize + " -> " + newSize + " bytes, verified integrity). Real install untouched.");
            return;
        }

        if (!wasAlreadyPatched || !File.Exists(backupPath))
            File.Copy(asarPath, backupPath, true);
        File.Delete(asarPath);
        File.Move(outPath, asarPath);
        Log(logPath, cliMode, "Patched " + targetName + " -> " + targetUrl + " (" + model + "). Backup saved to server.asar.bak.");
    }

    static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    static string BuildStateKey(
        string asarPath,
        string targetUrl,
        string model,
        bool skipAsar,
        string appAsarPath,
        string iconPath,
        string customName,
        bool skipIcon,
        bool skipName,
        string aumid,
        string stateDir)
    {
        string key;
        if (skipAsar)
        {
            key = "v8|skip-asar";
        }
        else
        {
            var info = new FileInfo(asarPath);
            key = "v8|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + targetUrl + "|" + model;
        }
        if (skipIcon) key += "|skip-icon";
        if (skipName) key += "|skip-name";
        bool hasAppPatch = (!skipIcon && !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            || (!skipName && !string.IsNullOrEmpty(customName));
        if (hasAppPatch && File.Exists(appAsarPath))
        {
            var appInfo = new FileInfo(appAsarPath);
            key += "|" + appInfo.Length + "|" + appInfo.LastWriteTimeUtc.Ticks;
            if (!skipIcon && !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                var icoInfo = new FileInfo(iconPath);
                key += "|ico:" + iconPath.ToLowerInvariant() + ":" + icoInfo.Length + ":" + icoInfo.LastWriteTimeUtc.Ticks;
            }
            if (!skipName && !string.IsNullOrEmpty(customName))
                key += "|name:" + customName;
        }
        key += "|aumid:" + (aumid ?? "");
        key += "|sm:" + GetStartMenuSignature(stateDir);
        return key;
    }

    class AsarReplacement
    {
        public string Label;
        public string[] DirSegments;
        public string FileName;
        public Dictionary<string, object> Entry;
        public long OldOffset;
        public int OldSize;
        public byte[] NewBytes;
        public string NewHash;
        public long Delta { get { return NewBytes.Length - OldSize; } }
    }

    static void ShiftOffsetsMulti(Dictionary<string, object> node, List<AsarReplacement> reps)
    {
        object filesObj;
        if (!node.TryGetValue("files", out filesObj)) return;
        var files = filesObj as Dictionary<string, object>;
        if (files == null) return;

        foreach (var kv in files)
        {
            var entry = kv.Value as Dictionary<string, object>;
            if (entry == null) continue;
            if (entry.ContainsKey("files"))
            {
                ShiftOffsetsMulti(entry, reps);
            }
            else if (!entry.ContainsKey("unpacked") && entry.ContainsKey("offset"))
            {
                long off = long.Parse(entry["offset"].ToString());
                long delta = 0;
                for (int i = 0; i < reps.Count; i++)
                    if (off > reps[i].OldOffset) delta += reps[i].Delta;
                if (delta != 0)
                    entry["offset"] = (off + delta).ToString();
            }
        }
    }

    static string PatchMainWindowTitleSource(string text, string customName, out bool changed)
    {
        string jsLit = NewJson().Serialize(customName);
        string replacement =
            "/*T3GROQ_TITLE*/window.setTitle(" + jsLit + ");\n"
            + "\t\twindow.on(\"page-title-updated\", (event) => {\n"
            + "\t\t\tevent.preventDefault();\n"
            + "\t\t\twindow.setTitle(" + jsLit + ");\n"
            + "\t\t});/*END_T3GROQ_TITLE*/";

        if (text.Contains(replacement))
        {
            changed = false;
            return text;
        }

        var existingRe = new Regex(@"/\*T3GROQ_TITLE\*/[\s\S]*?/\*END_T3GROQ_TITLE\*/");
        if (existingRe.IsMatch(text))
        {
            changed = true;
            return existingRe.Replace(text, replacement.Replace("$", "$$"), 1);
        }

        var origRe = new Regex(
            @"window\.on\(""page-title-updated"",\s*\(event,\s*title\)\s*=>\s*\{\s*if\s*\(title\.trim\(\)\s*!==\s*""""\)\s*return;\s*event\.preventDefault\(\);\s*window\.setTitle\(environment\.displayName\);\s*\}\);");
        if (!origRe.IsMatch(text))
            throw new Exception("Patch failed: could not match page-title-updated handler in main.cjs.");

        changed = true;
        return origRe.Replace(text, replacement.Replace("$", "$$"), 1);
    }

    static string RestoreMainWindowTitleSource(string text, out bool changed)
    {
        var existingRe = new Regex(@"/\*T3GROQ_TITLE\*/[\s\S]*?/\*END_T3GROQ_TITLE\*/");
        if (!existingRe.IsMatch(text))
        {
            changed = false;
            return text;
        }
        const string original =
            "window.on(\"page-title-updated\", (event, title) => {\n"
            + "\t\t\tif (title.trim() !== \"\") return;\n"
            + "\t\t\tevent.preventDefault();\n"
            + "\t\t\twindow.setTitle(environment.displayName);\n"
            + "\t\t});";
        changed = true;
        return existingRe.Replace(text, original, 1);
    }

    static byte[] ReadIconFromAsar(string asarPath)
    {
        if (!File.Exists(asarPath)) return null;
        try
        {
            using (var s = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long headerSize;
                var tree = ReadAsarHeader(s, out headerSize);
                var prodRes = GetFilesDict(tree, "apps", "desktop", "prod-resources");
                if (prodRes == null || !prodRes.ContainsKey("icon.ico")) return null;
                var entry = prodRes["icon.ico"] as Dictionary<string, object>;
                if (entry == null || entry.ContainsKey("unpacked")) return null;
                long off = long.Parse(entry["offset"].ToString());
                int sz = Convert.ToInt32(entry["size"]);
                byte[] buf = new byte[sz];
                s.Seek(headerSize + off, SeekOrigin.Begin);
                ReadExact(s, buf, 0, sz);
                return buf;
            }
        }
        catch { return null; }
    }

    static void PatchAppAsar(
        string appAsarPath,
        string backupPath,
        byte[] newIconBytes,
        string customName,
        bool skipIcon,
        bool skipName,
        bool dryRun,
        string logPath,
        bool cliMode)
    {
        string outPath = dryRun
            ? Path.Combine(Path.GetTempPath(), "app-patch-dryrun-" + DateTime.UtcNow.Ticks + ".asar")
            : appAsarPath + ".tmp";

        var reps = new List<AsarReplacement>();
        byte[] targetIconBytes = skipIcon ? null : (newIconBytes ?? ReadIconFromAsar(backupPath));
        string iconActionLabel = newIconBytes != null ? "icon.ico" : "icon.ico (restored default)";

        using (var src = new FileStream(appAsarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long headerSize;
            var tree = ReadAsarHeader(src, out headerSize);

            if (!skipIcon && targetIconBytes != null)
            {
                var prodRes = GetFilesDict(tree, "apps", "desktop", "prod-resources");
                if (prodRes == null || !prodRes.ContainsKey("icon.ico"))
                    throw new Exception("Could not find apps/desktop/prod-resources/icon.ico in app.asar.");
                var entry = prodRes["icon.ico"] as Dictionary<string, object>;
                if (entry == null || entry.ContainsKey("unpacked"))
                    throw new Exception("apps/desktop/prod-resources/icon.ico is not a packed entry in app.asar.");

                long oldOffset = long.Parse(entry["offset"].ToString());
                int oldSize = Convert.ToInt32(entry["size"]);
                byte[] existing = new byte[oldSize];
                src.Seek(headerSize + oldOffset, SeekOrigin.Begin);
                ReadExact(src, existing, 0, oldSize);

                string newHash, oldHash;
                using (var sha = SHA256.Create())
                {
                    newHash = ToHex(sha.ComputeHash(targetIconBytes));
                    oldHash = ToHex(sha.ComputeHash(existing));
                }
                if (oldHash != newHash)
                {
                    reps.Add(new AsarReplacement
                    {
                        Label = iconActionLabel + " (" + oldSize + " -> " + targetIconBytes.Length + " bytes)",
                        DirSegments = new[] { "apps", "desktop", "prod-resources" },
                        FileName = "icon.ico",
                        Entry = entry,
                        OldOffset = oldOffset,
                        OldSize = oldSize,
                        NewBytes = targetIconBytes,
                        NewHash = newHash
                    });
                }
            }

            if (!skipName)
            {
                var distElectron = GetFilesDict(tree, "apps", "desktop", "dist-electron");
                if (distElectron == null || !distElectron.ContainsKey("main.cjs"))
                    throw new Exception("Could not find apps/desktop/dist-electron/main.cjs in app.asar.");
                var entry = distElectron["main.cjs"] as Dictionary<string, object>;
                if (entry == null || entry.ContainsKey("unpacked"))
                    throw new Exception("apps/desktop/dist-electron/main.cjs is not a packed entry in app.asar.");

                long oldOffset = long.Parse(entry["offset"].ToString());
                int oldSize = Convert.ToInt32(entry["size"]);
                byte[] existing = new byte[oldSize];
                src.Seek(headerSize + oldOffset, SeekOrigin.Begin);
                ReadExact(src, existing, 0, oldSize);
                string mainText = Encoding.UTF8.GetString(existing);

                bool changed;
                string patchedMain = !string.IsNullOrEmpty(customName)
                    ? PatchMainWindowTitleSource(mainText, customName, out changed)
                    : RestoreMainWindowTitleSource(mainText, out changed);
                if (changed)
                {
                    byte[] newBytes = Encoding.UTF8.GetBytes(patchedMain);
                    string newHash;
                    using (var sha = SHA256.Create()) newHash = ToHex(sha.ComputeHash(newBytes));
                    reps.Add(new AsarReplacement
                    {
                        Label = !string.IsNullOrEmpty(customName)
                            ? "main.cjs title -> \"" + customName + "\""
                            : "main.cjs title -> default",
                        DirSegments = new[] { "apps", "desktop", "dist-electron" },
                        FileName = "main.cjs",
                        Entry = entry,
                        OldOffset = oldOffset,
                        OldSize = oldSize,
                        NewBytes = newBytes,
                        NewHash = newHash
                    });
                }
            }

            if (reps.Count == 0)
            {
                Log(logPath, cliMode, "app.asar already matches custom icon/name.");
                return;
            }

            reps.Sort((a, b) => a.OldOffset.CompareTo(b.OldOffset));

            foreach (var r in reps)
            {
                r.Entry["size"] = r.NewBytes.Length;
                if (r.Entry.ContainsKey("integrity"))
                {
                    var oldInt = r.Entry["integrity"] as Dictionary<string, object>;
                    int blockSize = (oldInt != null && oldInt.ContainsKey("blockSize"))
                        ? Convert.ToInt32(oldInt["blockSize"])
                        : 4 * 1024 * 1024;
                    r.Entry["integrity"] = ComputeIntegrity(r.NewBytes, blockSize);
                }
            }
            ShiftOffsetsMulti(tree, reps);

            byte[] newHeader = EncodeAsarHeader(tree);
            using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                dst.Write(newHeader, 0, newHeader.Length);
                long cursor = 0;
                foreach (var r in reps)
                {
                    CopyRange(src, dst, headerSize + cursor, r.OldOffset - cursor);
                    dst.Write(r.NewBytes, 0, r.NewBytes.Length);
                    cursor = r.OldOffset + r.OldSize;
                }
                CopyRange(src, dst, headerSize + cursor, (src.Length - headerSize) - cursor);
            }
        }

        // Verify rewritten app.asar integrity before swapping
        using (var ver = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long vHeader;
            var vTree = ReadAsarHeader(ver, out vHeader);
            foreach (var r in reps)
            {
                var vDir = GetFilesDict(vTree, r.DirSegments);
                var vEntry = vDir[r.FileName] as Dictionary<string, object>;
                long vOff = long.Parse(vEntry["offset"].ToString());
                int vSz = Convert.ToInt32(vEntry["size"]);
                byte[] vBuf = new byte[vSz];
                ver.Seek(vHeader + vOff, SeekOrigin.Begin);
                ReadExact(ver, vBuf, 0, vSz);
                using (var sha = SHA256.Create())
                {
                    if (ToHex(sha.ComputeHash(vBuf)) != r.NewHash)
                        throw new Exception("Verification failed: " + r.FileName + " SHA-256 mismatch in rewritten app.asar.");
                }
            }
        }

        var labels = new List<string>(reps.Count);
        foreach (var r in reps) labels.Add(r.Label);
        string summary = string.Join(", ", labels.ToArray());

        if (dryRun)
        {
            File.Delete(outPath);
            Log(logPath, cliMode, "DRY RUN PASSED: app.asar patched [" + summary + "] (verified integrity). Real install untouched.");
            return;
        }

        if (!File.Exists(backupPath))
            File.Copy(appAsarPath, backupPath, true);
        File.Delete(appAsarPath);
        File.Move(outPath, appAsarPath);
        Log(logPath, cliMode, "Patched app.asar [" + summary + "].");
    }

    // --- Icon extraction (.ico directly, or primary icon group from .exe/.dll) ---

    const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    static readonly IntPtr RT_ICON = (IntPtr)3;
    static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);
    delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LockResource(IntPtr hResData);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    static byte[] ReadWin32Resource(IntPtr hMod, IntPtr name, IntPtr type)
    {
        IntPtr hRes = FindResource(hMod, name, type);
        if (hRes == IntPtr.Zero) throw new Exception("Win32 icon resource not found.");
        uint size = SizeofResource(hMod, hRes);
        IntPtr ptr = LockResource(LoadResource(hMod, hRes));
        byte[] data = new byte[size];
        Marshal.Copy(ptr, data, 0, (int)size);
        return data;
    }

    static byte[] LoadIconBytes(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Icon source not found: " + path);
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(path);

        IntPtr hMod = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hMod == IntPtr.Zero)
            throw new Exception("Could not load icon resource from: " + path);
        try
        {
            byte[] grp = null;
            EnumResourceNames(hMod, RT_GROUP_ICON, (m, t, n, p) => { grp = ReadWin32Resource(m, n, RT_GROUP_ICON); return false; }, IntPtr.Zero);
            if (grp == null)
                throw new Exception("No icon group resource found in: " + path);
            ushort count = BitConverter.ToUInt16(grp, 4);
            var images = new List<byte[]>(count);
            var entries = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 14;
                ushort id = BitConverter.ToUInt16(grp, off + 12);
                images.Add(ReadWin32Resource(hMod, (IntPtr)id, RT_ICON));
                byte[] ent = new byte[12];
                Buffer.BlockCopy(grp, off, ent, 0, 12);
                entries.Add(ent);
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write(count);
                uint dataOffset = (uint)(6 + count * 16);
                for (int i = 0; i < count; i++)
                {
                    bw.Write(entries[i], 0, 8);
                    bw.Write((uint)images[i].Length);
                    bw.Write(dataOffset);
                    dataOffset += (uint)images[i].Length;
                }
                for (int i = 0; i < count; i++) bw.Write(images[i]);
                return ms.ToArray();
            }
        }
        finally
        {
            FreeLibrary(hMod);
        }
    }

    static void CopyRange(Stream src, Stream dst, long start, long length)
    {
        src.Seek(start, SeekOrigin.Begin);
        byte[] buf = new byte[4 * 1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buf.Length, remaining);
            int read = src.Read(buf, 0, toRead);
            if (read <= 0) throw new EndOfStreamException("Unexpected EOF while copying ASAR data.");
            dst.Write(buf, 0, read);
            remaining -= read;
        }
    }

    static void ReadExact(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading ASAR.");
            total += n;
        }
    }

    static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    // --- Shortcut + AppUserModelID (merges pinned launcher with running T3 Code taskbar button) ---

    static readonly PropertyKey PKEY_AppUserModel_ID =
        new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    static readonly PropertyKey PKEY_AppUserModel_ToastActivatorCLSID =
        new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 26);

    static readonly string[] StockStartMenuNames = { "T3 Code (Nightly).lnk", "T3 Code (Beta).lnk", "T3 Code.lnk" };

    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    static string GetStartMenuProgramsDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
    }

    static void SaveAumid(string stateDir, string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        try
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "aumid.txt"), aumid);
        }
        catch { }
    }

    static void SaveToastActivatorClsid(string stateDir, Guid? clsid)
    {
        if (!clsid.HasValue || clsid.Value == Guid.Empty) return;
        try
        {
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "toast-clsid.txt"), clsid.Value.ToString("B"));
        }
        catch { }
    }

    static bool IsToastClsidRegistered(Guid clsid, string exePath)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + clsid.ToString("B") + @"\LocalServer32"))
            {
                if (key == null) return false;
                string val = key.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(val)) return false;
                if (string.IsNullOrEmpty(exePath)) return true;
                string cleaned = val.Trim().Trim('"');
                return string.Equals(cleaned, exePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
    }

    static Guid? DetectToastActivatorClsid(string stateDir, string exePath)
    {
        string programs = GetStartMenuProgramsDir();
        Guid? fallback = null;

        // 1. Check existing Start Menu shortcuts (stock + currently managed)
        var candidates = new List<string>(StockStartMenuNames);
        string managedFile = Path.Combine(stateDir, "startmenu-managed.txt");
        if (File.Exists(managedFile))
        {
            string m = File.ReadAllText(managedFile).Trim();
            if (!string.IsNullOrEmpty(m)) candidates.Add(m);
        }
        foreach (string name in candidates)
        {
            string p = Path.Combine(programs, name);
            if (!File.Exists(p)) continue;
            Guid? id = ReadShortcutToastActivatorClsid(p);
            if (id.HasValue && id.Value != Guid.Empty)
            {
                if (IsToastClsidRegistered(id.Value, exePath)) return id;
                if (!fallback.HasValue) fallback = id;
            }
        }

        // 2. Check saved toast-clsid.txt
        string savedPath = Path.Combine(stateDir, "toast-clsid.txt");
        if (File.Exists(savedPath))
        {
            Guid parsed;
            if (Guid.TryParse(File.ReadAllText(savedPath).Trim(), out parsed) && parsed != Guid.Empty)
            {
                if (IsToastClsidRegistered(parsed, exePath)) return parsed;
                if (!fallback.HasValue) fallback = parsed;
            }
        }

        // 3. Check Start Menu backup
        string smBackup = Path.Combine(stateDir, "startmenu.lnk.bak");
        if (File.Exists(smBackup))
        {
            Guid? id = ReadShortcutToastActivatorClsid(smBackup);
            if (id.HasValue && id.Value != Guid.Empty)
            {
                if (IsToastClsidRegistered(id.Value, exePath)) return id;
                if (!fallback.HasValue) fallback = id;
            }
        }

        // 4. Scan HKCU\Software\Classes\CLSID\*\LocalServer32 for T3 Code's registered ToastActivatorCLSID
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                using (var clsidRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID"))
                {
                    if (clsidRoot != null)
                    {
                        foreach (string sub in clsidRoot.GetSubKeyNames())
                        {
                            Guid parsed;
                            if (!Guid.TryParse(sub, out parsed) || parsed == Guid.Empty) continue;
                            if (IsToastClsidRegistered(parsed, exePath)) return parsed;
                        }
                    }
                }
            }
            catch { }
        }

        return fallback;
    }

    static string ResolveAumid(string stateDir, string explicitAumid)
    {
        if (!string.IsNullOrWhiteSpace(explicitAumid))
            return explicitAumid.Trim();
        string baseAumid = DetectT3Aumid(stateDir) ?? DefaultAumid;
        if (!baseAumid.EndsWith(".groq", StringComparison.OrdinalIgnoreCase))
            baseAumid += ".groq";
        return baseAumid;
    }

    static string DetectT3Aumid(string stateDir)
    {
        string programs = GetStartMenuProgramsDir();
        foreach (string name in StockStartMenuNames)
        {
            string p = Path.Combine(programs, name);
            if (!File.Exists(p)) continue;
            string id = ReadShortcutAumid(p);
            if (!string.IsNullOrEmpty(id) && !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        string smBackup = Path.Combine(stateDir, "startmenu.lnk.bak");
        if (File.Exists(smBackup))
        {
            string id = ReadShortcutAumid(smBackup);
            if (!string.IsNullOrEmpty(id) && !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        string aumidPath = Path.Combine(stateDir, "aumid.txt");
        if (File.Exists(aumidPath))
        {
            string saved = File.ReadAllText(aumidPath).Trim();
            if (!string.IsNullOrEmpty(saved)) return saved;
        }
        return null;
    }

    static string GetStartMenuSignature(string stateDir)
    {
        string programs = GetStartMenuProgramsDir();
        var parts = new List<string>();
        foreach (string name in StockStartMenuNames)
        {
            string p = Path.Combine(programs, name);
            if (File.Exists(p))
            {
                var fi = new FileInfo(p);
                parts.Add(name + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks);
            }
        }
        string managedFile = Path.Combine(stateDir, "startmenu-managed.txt");
        if (File.Exists(managedFile))
        {
            string managedName = File.ReadAllText(managedFile).Trim();
            if (!string.IsNullOrEmpty(managedName))
            {
                string mp = Path.Combine(programs, managedName);
                if (File.Exists(mp))
                {
                    var fi = new FileInfo(mp);
                    parts.Add("m:" + managedName + ":" + fi.Length + ":" + fi.LastWriteTimeUtc.Ticks);
                }
                else
                {
                    parts.Add("m:" + managedName + ":missing");
                }
            }
        }
        return string.Join(",", parts.ToArray());
    }

    static void SyncStartMenuShortcut(
        string stateDir,
        string selfExe,
        string exePath,
        string activeIconPath,
        string activeName,
        string targetUrl,
        string model,
        bool skipAsar,
        bool skipIcon,
        bool skipName,
        string aumid,
        string logPath,
        bool cliMode)
    {
        string programs = GetStartMenuProgramsDir();
        if (!Directory.Exists(programs)) return;

        string smBackupPath = Path.Combine(stateDir, "startmenu.lnk.bak");
        string smOrigNamePath = Path.Combine(stateDir, "startmenu-orig-name.txt");
        string smManagedPath = Path.Combine(stateDir, "startmenu-managed.txt");

        // Backup the stock Start Menu shortcut if present and not already pointing to us
        foreach (string stockName in StockStartMenuNames)
        {
            string p = Path.Combine(programs, stockName);
            if (!File.Exists(p)) continue;
            string target = ReadShortcutTarget(p);
            bool isOurs = !string.IsNullOrEmpty(target)
                && string.Equals(Path.GetFullPath(target), Path.GetFullPath(selfExe), StringComparison.OrdinalIgnoreCase);
            if (!isOurs)
            {
                Directory.CreateDirectory(stateDir);
                if (!File.Exists(smBackupPath))
                {
                    File.Copy(p, smBackupPath, true);
                    File.WriteAllText(smOrigNamePath, stockName);
                }
                Guid? detectedToast = ReadShortcutToastActivatorClsid(p);
                if (detectedToast.HasValue) SaveToastActivatorClsid(stateDir, detectedToast);
                break;
            }
        }

        Guid? toastClsid = DetectToastActivatorClsid(stateDir, exePath);
        SaveToastActivatorClsid(stateDir, toastClsid);

        string origName = File.Exists(smOrigNamePath) ? File.ReadAllText(smOrigNamePath).Trim() : "T3 Code (Nightly).lnk";
        if (string.IsNullOrEmpty(origName)) origName = "T3 Code (Nightly).lnk";

        string desiredFileName = !string.IsNullOrEmpty(activeName)
            ? SanitizeFileName(activeName) + ".lnk"
            : origName;
        string desiredPath = Path.Combine(programs, desiredFileName);

        // Remove any previously managed Start Menu shortcut with a different name
        if (File.Exists(smManagedPath))
        {
            string prevName = File.ReadAllText(smManagedPath).Trim();
            if (!string.IsNullOrEmpty(prevName)
                && !string.Equals(prevName, desiredFileName, StringComparison.OrdinalIgnoreCase))
            {
                string prevPath = Path.Combine(programs, prevName);
                if (File.Exists(prevPath))
                {
                    try { File.Delete(prevPath); } catch { }
                }
            }
        }

        // Remove any stock T3 Code Start Menu shortcut with a different name so Start Menu doesn't
        // resolve the shared AppUserModelID back to "T3 Code (Nightly)"
        foreach (string stockName in StockStartMenuNames)
        {
            if (string.Equals(stockName, desiredFileName, StringComparison.OrdinalIgnoreCase)) continue;
            string p = Path.Combine(programs, stockName);
            if (!File.Exists(p)) continue;
            try { File.Delete(p); } catch { }
        }

        string lnkArgs = BuildShortcutArgs(targetUrl, model, skipAsar, skipIcon, skipName, activeIconPath, activeName);
        string iconPath = !string.IsNullOrEmpty(activeIconPath)
            ? activeIconPath
            : (File.Exists(exePath) ? exePath : selfExe);
        string desc = !string.IsNullOrEmpty(activeName)
            ? activeName
            : Path.GetFileNameWithoutExtension(desiredFileName);

        CreateMergedShortcut(desiredPath, selfExe, lnkArgs, iconPath, aumid, toastClsid, desc);
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(smManagedPath, desiredFileName);
        SaveAumid(stateDir, aumid);
        SaveToastActivatorClsid(stateDir, toastClsid);
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        Log(logPath, cliMode, "Synced Start Menu shortcut: " + desiredPath
            + " (AppUserModelID=" + aumid
            + (toastClsid.HasValue ? ", ToastActivatorCLSID=" + toastClsid.Value.ToString("B") : "")
            + ")");
    }

    static bool RestoreStartMenuShortcut(string stateDir, string logPath, bool cliMode)
    {
        string programs = GetStartMenuProgramsDir();
        string smBackupPath = Path.Combine(stateDir, "startmenu.lnk.bak");
        string smOrigNamePath = Path.Combine(stateDir, "startmenu-orig-name.txt");
        string smManagedPath = Path.Combine(stateDir, "startmenu-managed.txt");

        bool changed = false;
        string origName = File.Exists(smOrigNamePath) ? File.ReadAllText(smOrigNamePath).Trim() : null;

        if (File.Exists(smManagedPath))
        {
            string managedName = File.ReadAllText(smManagedPath).Trim();
            if (!string.IsNullOrEmpty(managedName))
            {
                string mp = Path.Combine(programs, managedName);
                if (File.Exists(mp))
                {
                    try { File.Delete(mp); changed = true; } catch { }
                }
            }
            try { File.Delete(smManagedPath); } catch { }
        }

        if (File.Exists(smBackupPath) && !string.IsNullOrEmpty(origName) && Directory.Exists(programs))
        {
            string dest = Path.Combine(programs, origName);
            File.Copy(smBackupPath, dest, true);
            changed = true;
            Log(logPath, cliMode, "Restored original Start Menu shortcut: " + dest);
        }

        if (changed)
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        return changed;
    }

    static string ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch { return null; }
    }

    static string ReadShortcutAumid(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var store = (IPropertyStore)link;
            PropertyKey key = PKEY_AppUserModel_ID;
            PropVariant val;
            store.GetValue(ref key, out val);
            try { return val.GetString(); }
            finally { val.Clear(); }
        }
        catch { return null; }
    }

    static Guid? ReadShortcutToastActivatorClsid(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var store = (IPropertyStore)link;
            PropertyKey key = PKEY_AppUserModel_ToastActivatorCLSID;
            PropVariant val;
            store.GetValue(ref key, out val);
            try { return val.GetGuid(); }
            finally { val.Clear(); }
        }
        catch { return null; }
    }

    static void CreateMergedShortcut(string shortcutPath, string targetExe, string arguments, string iconPath, string aumid, Guid? toastClsid, string description)
    {
        string dir = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var link = (IShellLinkW)new CShellLink();
        link.SetPath(targetExe);
        if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
        link.SetWorkingDirectory(Path.GetDirectoryName(targetExe));
        link.SetDescription(description);
        if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);

        var store = (IPropertyStore)link;
        PropertyKey key = PKEY_AppUserModel_ID;
        var val = PropVariant.FromString(aumid);
        try
        {
            store.SetValue(ref key, ref val);
        }
        finally { val.Clear(); }

        if (toastClsid.HasValue && toastClsid.Value != Guid.Empty)
        {
            PropertyKey toastKey = PKEY_AppUserModel_ToastActivatorCLSID;
            var toastVal = PropVariant.FromGuid(toastClsid.Value);
            try
            {
                store.SetValue(ref toastKey, ref toastVal);
            }
            finally { toastVal.Clear(); }
        }

        store.Commit();

        ((IPersistFile)link).Save(shortcutPath, true);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid guid, uint id) { fmtid = guid; pid = id; }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;

        const ushort VT_LPWSTR = 31;
        const ushort VT_CLSID = 72;

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pvar);

        public static PropVariant FromString(string s)
        {
            return new PropVariant { vt = VT_LPWSTR, ptr = Marshal.StringToCoTaskMemUni(s) };
        }

        public static PropVariant FromGuid(Guid g)
        {
            byte[] bytes = g.ToByteArray();
            IntPtr mem = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            return new PropVariant { vt = VT_CLSID, ptr = mem };
        }

        public string GetString()
        {
            return (vt == VT_LPWSTR && ptr != IntPtr.Zero) ? Marshal.PtrToStringUni(ptr) : null;
        }

        public Guid? GetGuid()
        {
            if (vt != VT_CLSID || ptr == IntPtr.Zero) return null;
            byte[] bytes = new byte[16];
            Marshal.Copy(ptr, bytes, 0, 16);
            return new Guid(bytes);
        }

        public void Clear() { PropVariantClear(ref this); }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }
}
