using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Automation;
using Fyltrix.Core.Imaging;

namespace Fyltrix.UI;

/// <summary>Manage automation rules, try them with a dry run, and watch the log.</summary>
public sealed class AutomationWindow : ToolWindow
{
    public sealed class RuleRow
    {
        public required AutomationRule Rule { get; init; }
        public string Name => Rule.Name;
        public string State => !Rule.Enabled ? "off" : Rule.Validate() is { } e ? "⚠ " + e : "on";
        public Brush StateBrush => Theme.Brush(!Rule.Enabled ? "Fg3" : Rule.Validate() != null ? "Danger" : "Accent");
        public string Summary => Rule.Summary();
    }

    public sealed class LogRow
    {
        public required AutomationLogEntry Entry { get; init; }
        public string Time => Entry.Time.ToString("T");
        public string Rule => Entry.Rule;
        public string File => Path.GetFileName(Entry.File);
        public string Outcome => (Entry.DryRun ? "[dry run] " : "") + Entry.Outcome;
        public Brush Brush => Theme.Brush(Entry.Success ? (Entry.DryRun ? "Fg2" : "Fg1") : "Danger");
    }

    private readonly AppSettings _settings;
    private readonly Func<AutomationEngine?> _engine;
    private readonly Action _restart;
    private readonly List<AutomationRule> _rules;
    private readonly ListView _ruleList, _logList;
    private readonly CheckBox _master = new() { Content = $"Run automation rules while {ProductBranding.Name} is open", FontWeight = FontWeights.SemiBold };
    private readonly List<LogRow> _log = new();

    public AutomationWindow(Window owner, AppSettings settings, Func<AutomationEngine?> engine, Action restart) : base(owner, "Automation", 1150, 760)
    {
        _settings = settings;
        _engine = engine;
        _restart = restart;
        _rules = AutomationStore.Load();
        _master.IsChecked = settings.AutomationEnabled;
        _master.Checked += (_, _) => { _settings.AutomationEnabled = true; _settings.Save(); _restart(); RefreshRules(); };
        _master.Unchecked += (_, _) => { _settings.AutomationEnabled = false; _settings.Save(); _restart(); RefreshRules(); };

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(_master);
        top.Children.Add(Dialogs.Note("Rules watch a folder and act on matching files: move, copy, rename, extract, compress, convert, file by date or run a program you choose. " +
                                      "Files are handled once, after they finish downloading or copying. Nothing is overwritten. Use Dry run to see what a rule would do.", "Fg2"));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        void B(string text, Action a, bool primary = false)
        {
            var b = Dialogs.Button(text, primary);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Click += (_, _) => a();
            buttons.Children.Add(b);
        }
        B("Add rule…", Add, primary: true);
        B("Edit…", Edit);
        B("Duplicate", Duplicate);
        B("Turn on/off", Toggle);
        B("Delete", Delete);
        B("Dry run", () => _ = RunSelected(dryRun: true));
        B("Run now", () => _ = RunSelected(dryRun: false));
        DockPanel.SetDock(buttons, Dock.Top);
        root.Children.Add(buttons);

        _ruleList = List(Column("Rule", nameof(RuleRow.Name), 200), Column("State", nameof(RuleRow.State), 180, brushProperty: nameof(RuleRow.StateBrush)),
            Column("What it does", nameof(RuleRow.Summary), 700, brush: "Fg2"));
        _ruleList.Height = 230;
        _ruleList.MouseDoubleClick += (_, _) => Edit();
        DockPanel.SetDock(_ruleList, Dock.Top);
        root.Children.Add(_ruleList);

        var logHeader = Dialogs.Label("Activity (also saved to %AppData%\\FileLens\\automation.log)");
        DockPanel.SetDock(logHeader, Dock.Top);
        root.Children.Add(logHeader);
        _logList = List(Column("Time", nameof(LogRow.Time), 80, brush: "Fg2"), Column("Rule", nameof(LogRow.Rule), 150), Column("File", nameof(LogRow.File), 220),
            Column("Result", nameof(LogRow.Outcome), 620, brushProperty: nameof(LogRow.Brush)));
        root.Children.Add(_logList);
        Content = root;

        if (engine() is { } e)
        {
            foreach (var entry in e.Log) _log.Add(new LogRow { Entry = entry });
            e.Logged += OnLogged;
            Closed += (_, _) => e.Logged -= OnLogged;
        }
        RefreshLog();
        RefreshRules();
    }

    internal IReadOnlyList<AutomationRule> Rules => _rules;

    private void OnLogged(object? sender, AutomationLogEntry e) => Dispatcher.BeginInvoke(() => { _log.Add(new LogRow { Entry = e }); RefreshLog(); });

    private void RefreshLog()
    {
        _logList.ItemsSource = _log.AsEnumerable().Reverse().Take(300).ToList();
    }

    private void RefreshRules(int select = -1)
    {
        _ruleList.ItemsSource = _rules.Select(r => new RuleRow { Rule = r }).ToList();
        if (select >= 0 && select < _rules.Count) _ruleList.SelectedIndex = select;
    }

    private AutomationRule? Selected => _ruleList.SelectedItem is RuleRow r ? r.Rule : null;

    private void SaveAll()
    {
        AutomationStore.Save(_rules);
        _restart();
    }

    private void Add()
    {
        var r = new AutomationRule { WatchFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
        if (new RuleEditor(this, r).ShowDialog() != true) return;
        _rules.Add(r);
        SaveAll();
        RefreshRules(_rules.Count - 1);
    }

    private void Edit()
    {
        if (Selected is not { } r) return;
        var copy = Clone(r);
        if (new RuleEditor(this, copy).ShowDialog() != true) return;
        int i = _rules.IndexOf(r);
        _rules[i] = copy;
        SaveAll();
        RefreshRules(i);
    }

    private void Duplicate()
    {
        if (Selected is not { } r) return;
        var copy = Clone(r);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " (copy)";
        copy.Enabled = false;
        _rules.Add(copy);
        SaveAll();
        RefreshRules(_rules.Count - 1);
    }

    private void Toggle()
    {
        if (Selected is not { } r) return;
        r.Enabled = !r.Enabled;
        SaveAll();
        RefreshRules(_rules.IndexOf(r));
    }

    private void Delete()
    {
        if (Selected is not { } r || !Dialogs.Confirm(this, "Delete rule", $"Delete the rule '{r.Name}'?")) return;
        _rules.Remove(r);
        SaveAll();
        RefreshRules();
    }

    private async Task RunSelected(bool dryRun)
    {
        if (Selected is not { } r) { Dialogs.Error(this, "Automation", "Select a rule first."); return; }
        var engine = _engine();
        bool temporary = engine == null;
        engine ??= new AutomationEngine(_settings.ArchiveLimits());
        if (temporary) engine.Logged += OnLogged;
        try { await engine.RunNowAsync(r, dryRun); }
        finally
        {
            if (temporary) { engine.Logged -= OnLogged; engine.Dispose(); }
        }
    }

    private static AutomationRule Clone(AutomationRule r) =>
        System.Text.Json.JsonSerializer.Deserialize<AutomationRule>(System.Text.Json.JsonSerializer.Serialize(r))!;
}

/// <summary>Edit one automation rule.</summary>
public sealed class RuleEditor : DialogWindow
{
    public RuleEditor(Window owner, AutomationRule r) : base(owner, "Automation rule", 620)
    {
        var name = new TextBox { Text = r.Name };
        var watch = new TextBox { Text = r.WatchFolder };
        var subs = new CheckBox { Content = "Include subfolders", IsChecked = r.IncludeSubfolders, Margin = new Thickness(0, 4, 0, 0) };
        var trigger = Dialogs.Combo(new[] { ("When a new file appears", TriggerKind.NewFile), ("When a file is added or changed", TriggerKind.NewOrChangedFile),
            ("Every few minutes (scan the folder)", TriggerKind.Interval), ("Only when I press Run now", TriggerKind.Manual) }, r.Trigger);
        var interval = new TextBox { Text = r.IntervalMinutes.ToString(), Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
        var exts = new TextBox { Text = r.Extensions, ToolTip = "e.g. zip 7z rar — empty for any file" };
        var pattern = new TextBox { Text = r.NamePattern, ToolTip = "e.g. invoice* — empty for any name" };
        var minKb = new TextBox { Text = r.MinSizeKB?.ToString() ?? "", Width = 90 };
        var maxKb = new TextBox { Text = r.MaxSizeKB?.ToString() ?? "", Width = 90 };
        var action = Dialogs.Combo(new[] { ("Move to a folder", RuleAction.Move), ("Copy to a folder", RuleAction.Copy), ("Rename", RuleAction.Rename),
            ("Extract the archive into a folder", RuleAction.ExtractArchive), ("Compress into a ZIP", RuleAction.CompressToZip),
            ("Convert the image", RuleAction.ConvertImage), ("File into year\\month folders", RuleAction.OrganizeByDate), ("Run a program", RuleAction.RunCommand) }, r.Action);
        var target = new TextBox { Text = r.TargetFolder };
        var rename = new TextBox { Text = r.RenameTemplate };
        var convert = Dialogs.Combo(new[] { ("PNG", ImageFormat.Png), ("JPEG", ImageFormat.Jpeg), ("WebP", ImageFormat.WebP), ("TIFF", ImageFormat.Tiff) }, r.ConvertTo);
        var original = new TextBox { Text = r.MoveOriginalTo };
        var command = new TextBox { Text = r.Command };
        var args = new TextBox { Text = r.Arguments };

        AddContent(Dialogs.Field("Name", name));
        AddContent(Dialogs.Field("Watch this folder", Dialogs.FolderPicker(watch, this, "Folder to watch")));
        AddContent(subs);
        AddContent(Dialogs.Field("When", trigger));
        var intervalField = Dialogs.Field("Minutes between scans", interval);
        AddContent(intervalField);
        var cond = new Grid();
        cond.ColumnDefinitions.Add(new ColumnDefinition());
        cond.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cond.ColumnDefinitions.Add(new ColumnDefinition());
        var extField = Dialogs.Field("File types (extensions)", exts);
        var patField = Dialogs.Field("Name matches", pattern);
        Grid.SetColumn(patField, 2);
        cond.Children.Add(extField);
        cond.Children.Add(patField);
        AddContent(cond);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal };
        sizes.Children.Add(Dialogs.Field("Min size (KB)", minKb));
        sizes.Children.Add(new Border { Width = 12 });
        sizes.Children.Add(Dialogs.Field("Max size (KB)", maxKb));
        AddContent(sizes);
        AddContent(Dialogs.Field("Then", action));
        var targetField = Dialogs.Field("Target folder", Dialogs.FolderPicker(target, this, "Target folder"));
        var renameField = Dialogs.Field("New name ({name}, {date:yyyy-MM-dd}, {created:…}, {ext})", rename);
        var convertField = Dialogs.Field("Convert to", convert);
        var originalField = Dialogs.Field("Afterwards, move the original to (optional)", Dialogs.FolderPicker(original, this, "Folder for originals"));
        var commandField = Dialogs.Field("Program", Dialogs.FilePicker(command, this, "Program to run", "Programs and scripts|*.exe;*.cmd;*.bat;*.ps1|All files|*.*"));
        var argsField = Dialogs.Field("Arguments ({path}, {name}, {folder})", args);
        foreach (var f in new[] { targetField, renameField, convertField, originalField, commandField, argsField }) AddContent(f);
        var err = Dialogs.Note("", "Danger");
        AddContent(err);

        void Sync()
        {
            var a = Dialogs.Selected<RuleAction>(action);
            targetField.Visibility = a is RuleAction.Rename or RuleAction.RunCommand ? Visibility.Collapsed : Visibility.Visible;
            renameField.Visibility = a == RuleAction.Rename ? Visibility.Visible : Visibility.Collapsed;
            convertField.Visibility = a == RuleAction.ConvertImage ? Visibility.Visible : Visibility.Collapsed;
            originalField.Visibility = a is RuleAction.ExtractArchive or RuleAction.ConvertImage ? Visibility.Visible : Visibility.Collapsed;
            commandField.Visibility = argsField.Visibility = a == RuleAction.RunCommand ? Visibility.Visible : Visibility.Collapsed;
            intervalField.Visibility = Dialogs.Selected<TriggerKind>(trigger) == TriggerKind.Interval ? Visibility.Visible : Visibility.Collapsed;
        }
        action.SelectionChanged += (_, _) => Sync();
        trigger.SelectionChanged += (_, _) => Sync();
        Sync();

        AddButton("Save", () =>
        {
            long? Kb(TextBox b) => long.TryParse(b.Text.Trim(), out var v) && v >= 0 ? v : null;
            var c = new AutomationRule
            {
                Id = r.Id, Enabled = r.Enabled, Name = name.Text.Trim(), WatchFolder = watch.Text.Trim().Trim('"'), IncludeSubfolders = subs.IsChecked == true,
                Trigger = Dialogs.Selected<TriggerKind>(trigger), IntervalMinutes = int.TryParse(interval.Text, out var m) ? m : 0,
                Extensions = exts.Text.Trim(), NamePattern = pattern.Text.Trim(), MinSizeKB = Kb(minKb), MaxSizeKB = Kb(maxKb),
                Action = Dialogs.Selected<RuleAction>(action), TargetFolder = target.Text.Trim().Trim('"'), RenameTemplate = rename.Text,
                ConvertTo = Dialogs.Selected<ImageFormat>(convert), MoveOriginalTo = original.Text.Trim().Trim('"'), Command = command.Text.Trim().Trim('"'), Arguments = args.Text,
            };
            var problem = c.Validate();
            if (problem != null) { err.Text = problem; return; }
            if (c.Action == RuleAction.RunCommand && c.Command != r.Command &&
                !Dialogs.Confirm(this, "Run a program automatically?", $"This rule will run\n{c.Command}\non every matching file, without asking. Only use programs you trust.\n\nSave the rule?"))
                return;
            foreach (var p in typeof(AutomationRule).GetProperties()) p.SetValue(r, p.GetValue(c));
            DialogResult = true;
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
    }
}
