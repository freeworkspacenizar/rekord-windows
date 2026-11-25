using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using WpfClipboard = System.Windows.Clipboard;

namespace Rekord.SystemServices;

/// <summary>
/// Copies exported animated WebP/GIF files onto the Windows clipboard as CF_HDROP
/// so pasting into Discord, Slack, Chrome, Notion, WhatsApp, or Explorer sends the animated file cleanly.
/// </summary>
public static class ClipboardHelper
{
    public static bool CopyFileToClipboard(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        var files = new StringCollection { Path.GetFullPath(filePath) };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                WpfClipboard.SetFileDropList(files);
                return true;
            }
            catch
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
