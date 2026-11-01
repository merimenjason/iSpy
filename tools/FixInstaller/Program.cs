using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

// Installs (or removes) the fixes from the merimenjason/iSpy "perf" branch over an existing iSpy 7.2.6 install.
// The iSpy files it installs are embedded as payload/x64.zip and payload/x86.zip (built by
// .github/workflows/build.yml).
// Every file it replaces is backed up first, so "Restore" puts the install back exactly as it was.
internal static class Program
{
    private const string BackupFolderName = "patch-backup-gridfix";
    private const string AddedListName = "added-files.txt";

    private static string BuildInfo =>
        ((AssemblyInformationalVersionAttribute) Attribute.GetCustomAttribute(Assembly.GetExecutingAssembly(),
            typeof(AssemblyInformationalVersionAttribute)))?.InformationalVersion ?? "local build";

    private static int Main(string[] args)
    {
        Console.Title = "iSpy fixes";
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("Something went wrong: " + ex.Message);
            Pause();
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Console.WriteLine("iSpy fixes (" + BuildInfo + ")");
        Console.WriteLine("==========");
        Console.WriteLine("Grid view live again, less memory used by camera frames, resized recordings fixed.");
        Console.WriteLine();

        var folder = FindInstall(args);
        if (folder == null)
        {
            Console.WriteLine("Couldn't find iSpy. Type or drag in the folder that has iSpy.exe, then press Enter:");
            folder = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (!File.Exists(Path.Combine(folder, "iSpy.exe")))
            {
                Console.WriteLine("There's no iSpy.exe in that folder. Nothing was changed.");
                Pause();
                return 1;
            }
        }

        var exe = Path.Combine(folder, "iSpy.exe");
        var backup = Path.Combine(folder, BackupFolderName);
        var patched = Directory.Exists(backup);
        var arch = Is64Bit(exe) ? "x64" : "x86";
        var installedVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "unknown";

        Console.WriteLine("iSpy folder: " + folder);
        Console.WriteLine("Installed:   " + installedVersion + (arch == "x64" ? " (64-bit)" : " (32-bit)") +
                          (patched ? ", fix already installed" : ""));
        Console.WriteLine();
        Console.WriteLine("  1  Install the fix" + (patched ? " again" : ""));
        Console.WriteLine("  2  Remove the fix (restore the original files)");
        Console.WriteLine("  3  Save iSpy's latest error details to the desktop");
        Console.WriteLine("  4  Apply the recommended settings (GPU decoding, lower redraw rate)");
        Console.WriteLine("  5  Undo the recommended settings");
        Console.WriteLine("  Q  Quit without changing anything");
        Console.Write("Choose: ");
        var choice = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();

        if (choice == "1")
        {
            if (!installedVersion.StartsWith("7.2.6") && !patched)
            {
                Console.WriteLine();
                Console.WriteLine("This fix was built from iSpy 7.2.6. Your iSpy is " + installedVersion + ".");
                Console.Write("Install anyway? (y/N): ");
                if (!(Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                    return 0;
            }
            if (!StopISpy()) return 1;
            Install(folder, backup, arch);
            Console.WriteLine();
            Console.WriteLine("Installed. The originals are saved in " + backup);
            Console.WriteLine();
            OfferSettings(false);
            Console.WriteLine("Starting iSpy to check it works (this takes about 30 seconds)...");
            var started = DateTime.Now;
            if (StartsAndStaysUp(exe))
            {
                Console.WriteLine("iSpy is running with the fix. To undo it later, run this again and choose 2.");
                Pause();
                return 0;
            }
            Console.WriteLine();
            Console.WriteLine("iSpy didn't stay running, so the fix is being removed again.");
            var report = SaveErrorReport(started.AddSeconds(-5));
            StopISpy(false);
            Restore(folder, backup);
            Console.WriteLine("Your original iSpy files are back.");
            Console.WriteLine("Please send the error details saved to: " + report);
            Start(exe);
            Pause();
            return 1;
        }
        if (choice == "2")
        {
            if (!patched)
            {
                Console.WriteLine("The fix isn't installed in this folder, so there's nothing to restore.");
                Pause();
                return 0;
            }
            if (!StopISpy()) return 1;
            Restore(folder, backup);
            Console.WriteLine();
            Console.WriteLine("Restored the original iSpy files.");
            OfferStart(exe);
            return 0;
        }
        if (choice == "3")
        {
            Console.WriteLine("Saved to: " + SaveErrorReport(DateTime.Now.AddDays(-2)));
            Pause();
            return 0;
        }
        if (choice == "4")
        {
            if (!StopISpy()) return 1;
            if (OfferSettings(true))
                OfferStart(exe);
            else
                Pause();
            return 0;
        }
        if (choice == "5")
        {
            var folders = SettingsTuner.FindSettingsFolders().Where(SettingsTuner.HasBackup).ToList();
            var xml = ChooseSettingsFolder(folders);
            if (xml == null)
            {
                Console.WriteLine("There are no saved settings to go back to.");
                Pause();
                return 0;
            }
            if (!StopISpy()) return 1;
            SettingsTuner.Undo(xml);
            OfferStart(exe);
            return 0;
        }
        return 0;
    }

    // iSpy must already be stopped: it saves its settings when it exits, over any changes made here.
    private static bool OfferSettings(bool chosen)
    {
        Console.WriteLine("Recommended settings for speed:");
        Console.WriteLine("  - GPU decoding on for the RTSP (FFmpeg) cameras");
        Console.WriteLine("  - Main window redraws at most 10 times a second (instead of 20)");
        if (!chosen)
        {
            Console.Write("Apply them too? (y/N): ");
            if (!(Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Settings left as they are (choose 4 later to apply them).");
                Console.WriteLine();
                return false;
            }
        }
        var xml = ChooseSettingsFolder(SettingsTuner.FindSettingsFolders());
        if (xml == null)
        {
            Console.WriteLine("Couldn't find iSpy's settings (%AppData%\\iSpy\\XML). Nothing was changed.");
            return false;
        }
        SettingsTuner.Apply(xml);
        Console.WriteLine("Done. To go back, run this again and choose 5.");
        Console.WriteLine();
        return true;
    }

    private static string ChooseSettingsFolder(List<string> folders)
    {
        if (folders.Count <= 1)
            return folders.FirstOrDefault();
        Console.WriteLine("iSpy settings were found for more than one Windows user:");
        for (var i = 0; i < folders.Count; i++)
            Console.WriteLine("  " + (i + 1) + "  " + folders[i]);
        Console.Write("Which one? (number): ");
        return int.TryParse(Console.ReadLine(), out var n) && n >= 1 && n <= folders.Count ? folders[n - 1] : null;
    }

    private static string FindInstall(string[] args)
    {
        var candidates = new List<string>();
        if (args.Length > 0) candidates.Add(args[0].Trim('"'));
        var here = AppDomain.CurrentDomain.BaseDirectory;
        candidates.Add(here);
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "iSpy"));
        }
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "iSpy.exe")));
    }

    private static bool Is64Bit(string exe)
    {
        using (var r = new BinaryReader(File.OpenRead(exe)))
        {
            r.BaseStream.Seek(0x3c, SeekOrigin.Begin);
            var pe = r.ReadInt32();
            r.BaseStream.Seek(pe + 4, SeekOrigin.Begin);
            return r.ReadUInt16() == 0x8664;
        }
    }

    // iSpyMonitor restarts iSpy when it exits, so it has to stop first.
    private static bool StopISpy(bool ask = true)
    {
        var running = Process.GetProcessesByName("iSpyMonitor").Concat(Process.GetProcessesByName("iSpy")).ToList();
        if (running.Count == 0) return true;

        if (ask)
        {
        Console.WriteLine();
        Console.Write("iSpy is running. Close it now? Recording stops until it restarts. (Y/n): ");
        if ((Console.ReadLine() ?? "").Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Close iSpy (right-click its tray icon, Exit) and run this again. Nothing was changed.");
            Pause();
            return false;
        }
        }

        foreach (var p in Process.GetProcessesByName("iSpyMonitor"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { /* already gone */ }
        }
        foreach (var p in Process.GetProcessesByName("iSpy"))
        {
            try
            {
                // Ask nicely first so iSpy saves its settings, then force it.
                p.CloseMainWindow();
                if (!p.WaitForExit(20000))
                {
                    p.Kill();
                    p.WaitForExit(10000);
                }
            }
            catch { /* already gone */ }
        }
        Thread.Sleep(1000);
        if (Process.GetProcessesByName("iSpy").Any())
        {
            Console.WriteLine("iSpy didn't close. Exit it from the tray icon and run this again. Nothing was changed.");
            Pause();
            return false;
        }
        return true;
    }

    private static void Install(string folder, string backup, string arch)
    {
        var firstTime = !Directory.Exists(backup);
        Directory.CreateDirectory(backup);
        var addedList = Path.Combine(backup, AddedListName);
        var added = File.Exists(addedList) ? File.ReadAllLines(addedList).ToList() : new List<string>();

        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload." + arch + ".zip"))
        using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var target = Path.Combine(folder, entry.FullName);
                var saved = Path.Combine(backup, entry.FullName);
                // Only the first install backs up: on a re-install, the files there are already ours.
                if (firstTime)
                {
                    if (File.Exists(target))
                        File.Copy(target, saved, true);
                    else
                        added.Add(entry.FullName);
                }
                Console.WriteLine("  " + entry.FullName);
                entry.ExtractToFile(target, true);
            }
        }
        if (firstTime) File.WriteAllLines(addedList, added);
    }

    private static void Restore(string folder, string backup)
    {
        var addedList = Path.Combine(backup, AddedListName);
        if (File.Exists(addedList))
        {
            foreach (var name in File.ReadAllLines(addedList).Where(n => n.Length > 0))
            {
                var f = Path.Combine(folder, name);
                if (File.Exists(f)) File.Delete(f);
            }
        }
        foreach (var saved in Directory.GetFiles(backup))
        {
            var name = Path.GetFileName(saved);
            if (name == AddedListName) continue;
            Console.WriteLine("  " + name);
            File.Copy(saved, Path.Combine(folder, name), true);
        }
        Directory.Delete(backup, true);
    }

    private static void OfferStart(string exe)
    {
        Console.Write("Start iSpy now? (Y/n): ");
        if (!(Console.ReadLine() ?? "").Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
            Start(exe);
    }

    // Started through Explorer so iSpy runs as you normally would, not with this program's admin rights.
    private static void Start(string exe)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = true });
    }

    private static bool StartsAndStaysUp(string exe)
    {
        Start(exe);
        // Wait for it to appear, then make sure it's still there 30 seconds later (a failed start exits quickly).
        for (var i = 0; i < 20 && !Process.GetProcessesByName("iSpy").Any(); i++) Thread.Sleep(500);
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(1000);
            if (!Process.GetProcessesByName("iSpy").Any()) return false;
        }
        return true;
    }

    // iSpy logs a failed start to %AppData%\iSpy\log_*.htm and exits without a message; Windows logs crashes
    // in the Application event log. Collect both.
    private static string SaveErrorReport(DateTime since)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("iSpy fix error report, " + DateTime.Now);
        sb.AppendLine();
        var dirs = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iSpy") };
        try
        {
            foreach (var u in Directory.GetDirectories(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users")))
                dirs.Add(Path.Combine(u, "AppData", "Roaming", "iSpy"));
        }
        catch { /* no access */ }
        var logs = dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
            .SelectMany(d => { try { return Directory.GetFiles(d, "log_*.htm"); } catch { return new string[0]; } })
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).Take(2);
        foreach (var f in logs)
        {
            sb.AppendLine("=== " + f.FullName + " (" + f.LastWriteTime + ")");
            try
            {
                var text = File.ReadAllText(f.FullName);
                text = System.Text.RegularExpressions.Regex.Replace(text, "<br\\s*/?>|</tr>|</p>", "\n");
                text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
                text = System.Net.WebUtility.HtmlDecode(text);
                // Camera addresses can carry passwords: blank out anything like user:pass@
                text = System.Text.RegularExpressions.Regex.Replace(text, "//[^/@\\s]+@", "//***@");
                sb.AppendLine(text.Length > 30000 ? text.Substring(text.Length - 30000) : text);
            }
            catch (Exception ex) { sb.AppendLine("(couldn't read: " + ex.Message + ")"); }
        }
        try
        {
            sb.AppendLine("=== Windows Application event log");
            using (var log = new EventLog("Application"))
            {
                var n = log.Entries.Count;
                for (var i = n - 1; i >= 0 && i >= n - 300; i--)
                {
                    var e = log.Entries[i];
                    if (e.TimeGenerated < since) break;
                    if (e.EntryType != EventLogEntryType.Error) continue;
                    if (e.Message.IndexOf("ispy", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine(e.TimeGenerated + " " + e.Source);
                    sb.AppendLine(e.Message);
                    sb.AppendLine();
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("(couldn't read the event log: " + ex.Message + ")"); }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ispy-fix-error.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void Pause()
    {
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
}
