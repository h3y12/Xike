using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeGuard;

internal static class AlarmRegressionCheck
{
    private static Button? FindSave(DependencyObject root)
    {
        if (root is Button b && Equals(b.Content, "保存")) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            Button? found = FindSave(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    internal static bool Write(string path)
    {
        var log = new StringBuilder();
        var win = new AlarmWindow(new AlarmBook(), 30);
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -10000;
        win.Top = -10000;
        try
        {
            win.Show();
            win.UpdateLayout();
            win.AddNormalForTest("07:00", "保存回归", 0, true, Array.Empty<int>());
            Button save = FindSave(win) ?? throw new InvalidOperationException("Save button not found");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            log.AppendLine(win.Result?.Count == 1 && !win.IsVisible
                ? "PASS: modeless alarm editor saves and closes without exception"
                : "FAIL: save did not retain the alarm and close the editor");
        }
        catch (Exception ex) { log.AppendLine("FAIL: " + ex); }
        finally { win.Close(); }
        var main = new MainWindow();
        try
        {
            main.LoadAlarmsForTest(Array.Empty<Alarm>());
            main.OpenAlarmSettings();
            var editor = Application.Current.Windows.OfType<AlarmWindow>().Single();
            editor.AddNormalForTest("07:00", "落盘回归", 0, true, Array.Empty<int>());
            editor.UpdateLayout();
            (FindSave(editor) ?? throw new InvalidOperationException("Save button not found")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            log.AppendLine(main.AlarmsForTest.Count == 1 && AppConfig.Load().BuildAlarmBook().Count == 1
                ? "PASS: real MainWindow editor saves the alarm to disk and reloads it"
                : "FAIL: real editor did not persist the alarm");
            var once = new Alarm { Id = "once", TimeOfDay = new TimeSpan(7, 0, 0), Repeat = AlarmRepeat.Once, DeleteAfterRing = true };
            main.LoadAlarmsForTest(new[] { once });
            main.OpenAlarmSettings();
            var staleEditor = Application.Current.Windows.OfType<AlarmWindow>().Single();
            staleEditor.UpdateLayout();
            var at = new DateTime(2026, 10, 7, 7, 0, 0);
            main.TickAlarmsForTest(at);
            log.AppendLine(main.AlarmsForTest.Count == 0 ? "PASS: one-shot is removed when it rings" : "FAIL: one-shot remains after triggering");
            log.AppendLine(AppConfig.Load().BuildAlarmBook().Count == 0 ? "PASS: deletion is persisted before user dismissal" : "FAIL: deleted one-shot reappears on reload");
            (FindSave(staleEditor) ?? throw new InvalidOperationException("Save button not found")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            log.AppendLine(main.AlarmsForTest.Count == 0 && AppConfig.Load().BuildAlarmBook().Count == 0
                ? "PASS: saving the real stale editor does not resurrect a consumed alarm"
                : "FAIL: stale editor resurrected a consumed alarm");
            main.TickAlarmsForTest(at.AddSeconds(600));
            log.AppendLine(main.AlarmsForTest.Count == 0 ? "PASS: timeout does not restore a one-shot" : "FAIL: one-shot remains after timeout");
        }
        catch (Exception ex) { log.AppendLine("FAIL: " + ex); }
        finally { main.Close(); }
        File.WriteAllText(path, log.ToString());
        return SelfTest.ReportPassed(log.ToString());
    }
}
