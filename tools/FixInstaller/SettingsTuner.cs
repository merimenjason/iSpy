using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

// Applies the recommended performance settings to iSpy's own settings files (%AppData%\iSpy\XML\objects.xml
// and config.xml), with a backup to undo them. iSpy must not be running: it rewrites both files when it exits.
internal static class SettingsTuner
{
    private const string BackupPrefix = "backup-before-tuning-";

    /// <summary>The iSpy settings folders on this PC (normally just the current user's).</summary>
    public static List<string> FindSettingsFolders()
    {
        var dirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iSpy", "XML")
        };
        try
        {
            foreach (var u in Directory.GetDirectories(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "Users")))
                dirs.Add(Path.Combine(u, "AppData", "Roaming", "iSpy", "XML"));
        }
        catch
        {
            // no access to other profiles
        }
        return dirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => File.Exists(Path.Combine(d, "objects.xml")) && File.Exists(Path.Combine(d, "config.xml")))
            .ToList();
    }

    public static void Apply(string xmlFolder)
    {
        var objectsPath = Path.Combine(xmlFolder, "objects.xml");
        var configPath = Path.Combine(xmlFolder, "config.xml");

        var backup = Path.Combine(xmlFolder, BackupPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        File.Copy(objectsPath, Path.Combine(backup, "objects.xml"));
        File.Copy(configPath, Path.Combine(backup, "config.xml"));

        int gpu = 0, cameras = 0;
        var objects = Load(objectsPath);
        foreach (var camera in objects.Root?.Element("cameras")?.Elements("camera") ?? Enumerable.Empty<XElement>())
        {
            cameras++;
            var settings = camera.Element("settings");
            var isFfmpeg = settings?.Element("sourceindex")?.Value.Trim() == "2";

            // Graphics card decoding for FFmpeg (RTSP) cameras.
            if (isFfmpeg && settings.Attribute("useGPU")?.Value != "true")
            {
                settings.SetAttributeValue("useGPU", "true");
                gpu++;
            }

            // Motion detection and the pre-record buffer are left alone: recordings start on motion alerts.
        }
        Save(objects, objectsPath);

        var redraw = false;
        var config = Load(configPath);
        var maxRedraw = config.Root?.Element("MaxRedrawRate");
        if (maxRedraw != null && int.TryParse(maxRedraw.Value, out var rate) && rate > 10)
        {
            maxRedraw.Value = "10";
            redraw = true;
        }
        Save(config, configPath);

        Console.WriteLine("  " + cameras + " cameras found.");
        Console.WriteLine("  GPU decoding switched on:        " + gpu + " RTSP cameras");
        Console.WriteLine("  Max redraw rate:                 " + (redraw ? "lowered to 10" : "unchanged"));
        Console.WriteLine("  Your previous settings are saved in " + backup);
    }

    public static bool Undo(string xmlFolder)
    {
        var latest = Directory.GetDirectories(xmlFolder, BackupPrefix + "*").OrderByDescending(d => d).FirstOrDefault();
        if (latest == null)
            return false;
        File.Copy(Path.Combine(latest, "objects.xml"), Path.Combine(xmlFolder, "objects.xml"), true);
        File.Copy(Path.Combine(latest, "config.xml"), Path.Combine(xmlFolder, "config.xml"), true);
        Directory.Delete(latest, true);
        Console.WriteLine("  Restored your settings from before the change (" + Path.GetFileName(latest) + ").");
        return true;
    }

    public static bool HasBackup(string xmlFolder) =>
        Directory.GetDirectories(xmlFolder, BackupPrefix + "*").Any();

    // iSpy writes these as UTF-8 (with a byte order mark) whatever the XML declaration says, and reads them with
    // a StreamReader that goes by the byte order mark. Parse the text so the declaration's encoding is ignored,
    // and keep the layout as it is.
    private static XDocument Load(string path) =>
        XDocument.Parse(File.ReadAllText(path), LoadOptions.PreserveWhitespace);

    private static void Save(XDocument doc, string path)
    {
        var text = (doc.Declaration != null ? doc.Declaration + "\r\n" : "") +
                   doc.Root.ToString(SaveOptions.DisableFormatting);
        XDocument.Parse(text); // still valid?
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(true));
        File.Copy(temp, path, true);
        File.Delete(temp);
    }
}
