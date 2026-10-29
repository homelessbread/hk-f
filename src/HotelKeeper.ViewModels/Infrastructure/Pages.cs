using System.Collections.ObjectModel;
using System.Windows.Input;
using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>What every page needs: who is signed in, how to talk to the user, and how to move around.</summary>
public sealed class PageContext(Session session, IDialogService dialogs)
{
    public Session Session { get; } = session;
    public IDialogService Dialogs { get; } = dialogs;
    /// <summary>Set by the shell. Navigates to a page by its key.</summary>
    public Func<string, Task> Navigate { get; set; } = _ => Task.CompletedTask;
    /// <summary>Set by the shell. Refreshes the unread-notification counter.</summary>
    public Func<Task> RefreshBadges { get; set; } = () => Task.CompletedTask;
    /// <summary>Set by the shell. Opens the change-password dialog.</summary>
    public Func<Task> ChangePassword { get; set; } = () => Task.CompletedTask;
}

public abstract class PageViewModel(PageContext ctx, string title, string description) : ObservableObject
{
    bool busy; string? error, status;
    protected PageContext Ctx { get; } = ctx;
    protected Session Me => Ctx.Session;

    public string Title { get; } = title;
    public string Description { get; } = description;
    public bool IsBusy { get => busy; protected set => Set(ref busy, value); }
    /// <summary>Banner shown at the top of the page.</summary>
    public string? ErrorMessage { get => error; set { Set(ref error, value); Raise(nameof(HasError)); } }
    public string? StatusMessage { get => status; set { Set(ref status, value); Raise(nameof(HasStatus)); } }
    public bool HasError => !string.IsNullOrEmpty(error);
    public bool HasStatus => !string.IsNullOrEmpty(status);

    ICommand? refresh;
    public ICommand RefreshCommand => refresh ??= new AsyncCommand(() => ReloadAsync(), ShowError);

    public async Task ReloadAsync()
    {
        IsBusy = true;
        try { ErrorMessage = null; await LoadAsync(); }
        catch (Exception ex) { ErrorMessage = ErrorText.For(ex); }
        finally { IsBusy = false; }
    }

    protected abstract Task LoadAsync();

    public void ShowError(Exception ex) { StatusMessage = null; ErrorMessage = ErrorText.For(ex); }

    /// <summary>Runs a change, reports success or the error, then reloads the data.</summary>
    protected async Task DoAsync(Func<Task<string?>> work)
    {
        try
        {
            ErrorMessage = null;
            var msg = await work();
            StatusMessage = msg;
            await ReloadAsync();
            await Ctx.RefreshBadges();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    protected Task DoAsync(Func<Task> work, string? success) => DoAsync(async () => { await work(); return success; });
}

// ---- lists -----------------------------------------------------------------------------------------------------

public enum ColumnKind { Text, Badge, Money, Date, DateTime, Bool, Number }
public sealed record ColumnDef(string Header, string Path, ColumnKind Kind = ColumnKind.Text, double Width = 0);

public enum FilterKind { Tabs, Combo, Date, Integer }

/// <summary>A search-bar control above a list: tabs, a drop-down, a date or a number.</summary>
public sealed class FilterVm : ObservableObject
{
    Choice? selected; DateTime? date; string text = "";
    public required string Key { get; init; }
    public required string Label { get; init; }
    public FilterKind Kind { get; init; }
    IReadOnlyList<Choice> choices = Array.Empty<Choice>();
    public IReadOnlyList<Choice> Choices { get => choices; set => Set(ref choices, value); }
    internal void RaiseChoices() => Raise(nameof(Choices));
    /// <summary>Optional async source of choices (loaded on first display).</summary>
    public Func<Task<IReadOnlyList<Choice>>>? ChoiceLoader { get; init; }
    public bool IsTabs => Kind == FilterKind.Tabs;
    public bool IsCombo => Kind == FilterKind.Combo;
    public bool IsDate => Kind == FilterKind.Date;
    public bool IsInteger => Kind == FilterKind.Integer;
    public Choice? Selected { get => selected; set { if (Set(ref selected, value)) Changed?.Invoke(); } }
    public DateTime? Date { get => date; set { if (Set(ref date, value)) Changed?.Invoke(); } }
    public string Text { get => text; set { if (Set(ref text, value)) Changed?.Invoke(); } }
    public Action? Changed { get; set; }

    public object? Value => Kind switch
    {
        FilterKind.Date => Date,
        FilterKind.Integer => int.TryParse(Text, out var i) ? i : null,
        _ => Selected?.Value
    };
}

public sealed record ListQuery(string Search, IReadOnlyDictionary<string, object?> Filters)
{
    public string? Str(string key) => Filters.TryGetValue(key, out var v) ? v as string : null;
    public int? Int(string key) => Filters.TryGetValue(key, out var v) ? v as int? : null;
    public bool Flag(string key) => Filters.TryGetValue(key, out var v) && v is true;
    public DateTime? Day(string key) => Filters.TryGetValue(key, out var v) ? v as DateTime? : null;
    public string? SearchOrNull => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
}

/// <summary>A toolbar button on a list page. It is enabled only when its rule allows the selected row.</summary>
public sealed class PageAction : ObservableObject
{
    public required string Label { get; init; }
    public string Icon { get; init; } = "";
    public bool IsPrimary { get; init; }
    public bool IsDanger { get; init; }
    public bool NeedsSelection { get; init; } = true;
    public string Tip { get; init; } = "";
    public required ICommand Command { get; init; }
}

public sealed class ListPageViewModel : PageViewModel
{
    readonly Func<ListQuery, Task<IReadOnlyList<object>>> loader;
    readonly List<RelayCommand> selectionCommands = new();
    readonly List<AsyncCommand> actionCommands = new();
    object? selected; string search = ""; bool loadedChoices; string countText = "";

    public ListPageViewModel(PageContext ctx, string title, string description, IReadOnlyList<ColumnDef> columns,
        Func<ListQuery, Task<IReadOnlyList<object>>> loader, string emptyText = "Nothing to show here yet.", bool searchable = true,
        string searchHint = "Search…") : base(ctx, title, description)
    {
        Columns = columns; this.loader = loader; EmptyText = emptyText; IsSearchable = searchable; SearchHint = searchHint;
        SearchCommand = new AsyncCommand(() => ReloadAsync(), ShowError);
    }

    public IReadOnlyList<ColumnDef> Columns { get; }
    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<PageAction> Actions { get; } = new();
    public ObservableCollection<FilterVm> Filters { get; } = new();
    public ObservableCollection<StatCard> Cards { get; } = new();
    public Func<IReadOnlyList<object>, IReadOnlyList<StatCard>>? SummaryCards { get; set; }
    public string EmptyText { get; }
    public bool IsSearchable { get; }
    public string SearchHint { get; }
    public ICommand SearchCommand { get; }
    public bool IsEmpty => Items.Count == 0 && !IsBusy;
    public bool HasCards => Cards.Count > 0;
    public string CountText { get => countText; private set => Set(ref countText, value); }

    public string SearchText { get => search; set => Set(ref search, value); }

    public object? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) { foreach (var c in selectionCommands) c.RaiseCanExecuteChanged(); foreach (var c in actionCommands) c.RaiseCanExecuteChanged(); } }
    }

    // ---- building -------------------------------------------------------------------------------------------
    public FilterVm AddTabs(string key, params (string label, object? value)[] tabs)
    {
        var f = new FilterVm { Key = key, Label = key, Kind = FilterKind.Tabs, Choices = tabs.Select(t => new Choice(t.value, t.label)).ToList() };
        f.Selected = f.Choices[0];
        f.Changed = () => _ = ReloadAsync();
        Filters.Add(f); return f;
    }

    public FilterVm AddCombo(string key, string label, IEnumerable<Choice>? choices = null, Func<Task<IReadOnlyList<Choice>>>? loader = null, bool includeAll = true, string allLabel = "All")
    {
        var list = new List<Choice>();
        if (includeAll) list.Add(new Choice(null, allLabel));
        if (choices != null) list.AddRange(choices);
        var f = new FilterVm { Key = key, Label = label, Kind = FilterKind.Combo, Choices = list, ChoiceLoader = loader };
        f.Selected = list.FirstOrDefault();
        f.Changed = () => { if (!suppress) _ = ReloadAsync(); };
        Filters.Add(f); return f;
    }

    public FilterVm AddDate(string key, string label, DateTime? value)
    {
        var f = new FilterVm { Key = key, Label = label, Kind = FilterKind.Date, Date = value };
        f.Changed = () => { if (!suppress) _ = ReloadAsync(); };
        Filters.Add(f); return f;
    }

    public FilterVm AddInteger(string key, string label, int value)
    {
        var f = new FilterVm { Key = key, Label = label, Kind = FilterKind.Integer, Text = value.ToString() };
        f.Changed = () => { if (!suppress) _ = ReloadAsync(); };
        Filters.Add(f); return f;
    }

    bool suppress;

    /// <summary>Adds a toolbar button for the selected row of type <typeparamref name="T"/>.</summary>
    public void Act<T>(string label, string icon, Func<T, Task> run, Func<T, bool>? can = null, bool primary = false, bool danger = false, string tip = "") where T : class
    {
        var cmd = new AsyncCommand(async _ => { if (Selected is T t) await run(t); }, ShowError, _ => Selected is T t && (can?.Invoke(t) ?? true));
        actionCommands.Add(cmd);
        Actions.Add(new PageAction { Label = label, Icon = icon, IsPrimary = primary, IsDanger = danger, NeedsSelection = true, Command = cmd, Tip = tip });
    }

    /// <summary>Adds a toolbar button that does not need a selected row (e.g. "Add room").</summary>
    public void Act(string label, string icon, Func<Task> run, bool primary = false, string tip = "")
    {
        var cmd = new AsyncCommand(_ => run(), ShowError);
        Actions.Add(new PageAction { Label = label, Icon = icon, IsPrimary = primary, NeedsSelection = false, Command = cmd, Tip = tip });
    }

    // ---- loading --------------------------------------------------------------------------------------------
    protected override async Task LoadAsync()
    {
        if (!loadedChoices)
        {
            loadedChoices = true;
            suppress = true;
            try
            {
                foreach (var f in Filters.Where(f => f.ChoiceLoader != null))
                {
                    var loaded = await f.ChoiceLoader!();
                    var all = f.Choices.Take(f.Choices.Count > 0 && f.Choices[0].Value is null ? 1 : 0).ToList();
                    all.AddRange(loaded);
                    f.Choices = all; f.Selected = all.FirstOrDefault();
                    f.NotifyChoices();
                }
            }
            finally { suppress = false; }
        }

        var filters = Filters.ToDictionary(f => f.Key, f => f.Value);
        var keep = (Selected as object);
        var rows = await loader(new ListQuery(SearchText, filters));
        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        Selected = null;
        CountText = rows.Count == 1 ? "1 record" : $"{rows.Count} records";
        Cards.Clear();
        if (SummaryCards != null) foreach (var c in SummaryCards(rows)) Cards.Add(c);
        Raise(nameof(HasCards));
        Raise(nameof(IsEmpty));
    }
}

static class FilterExt
{
    public static void NotifyChoices(this FilterVm f) => f.RaiseChoices();
}

// ---- form page (profile, change password …) ------------------------------------------------------------------

public sealed class FormPageViewModel : PageViewModel
{
    readonly Func<FormResult, Task<string?>> save;
    readonly Func<Task<IReadOnlyList<FormField>>> build;

    public FormPageViewModel(PageContext ctx, string title, string description, Func<Task<IReadOnlyList<FormField>>> build,
        Func<FormResult, Task<string?>> save, string saveText = "Save changes") : base(ctx, title, description)
    {
        this.build = build; this.save = save; SaveText = saveText;
        SaveCommand = new AsyncCommand(SubmitAsync, ShowError);
    }

    public string SaveText { get; }
    public ObservableCollection<FormField> Fields { get; } = new();
    public ICommand SaveCommand { get; }

    protected override async Task LoadAsync()
    {
        var fields = await build();
        Fields.Clear();
        foreach (var f in fields) Fields.Add(f);
    }

    async Task SubmitAsync()
    {
        ErrorMessage = null; StatusMessage = null;
        var values = new Dictionary<string, object?>();
        var ok = true;
        foreach (var f in Fields) { if (f.TryGet(out var v)) values[f.Key] = v; else ok = false; }
        if (!ok) { ErrorMessage = "Please correct the highlighted fields."; return; }
        IsBusy = true;
        try { StatusMessage = await save(new FormResult(values)); await ReloadAsync(); }
        finally { IsBusy = false; }
    }
}

// ---- dashboard --------------------------------------------------------------------------------------------------

public sealed record QuickAction(string Label, string Icon, string PageKey);

public sealed class DashboardPageViewModel : PageViewModel
{
    readonly Func<Task<DashboardData>> load;
    public DashboardPageViewModel(PageContext ctx, string title, string description, Func<Task<DashboardData>> load, IReadOnlyList<QuickAction>? quick = null)
        : base(ctx, title, description)
    {
        this.load = load;
        QuickActions = quick ?? Array.Empty<QuickAction>();
        QuickCommand = new AsyncCommand(async p => { if (p is string key && !string.IsNullOrWhiteSpace(key)) await Ctx.Navigate(key); }, ShowError);
        NavigateCardCommand = new AsyncCommand(async p => { if (p is string key && !string.IsNullOrWhiteSpace(key)) await Ctx.Navigate(key); }, ShowError);
    }

    public ObservableCollection<StatCard> Cards { get; } = new();
    public ObservableCollection<Panel> Panels { get; } = new();
    public IReadOnlyList<QuickAction> QuickActions { get; }
    public ICommand QuickCommand { get; }
    public ICommand NavigateCardCommand { get; }
    public bool HasQuickActions => QuickActions.Count > 0;

    protected override async Task LoadAsync()
    {
        var d = await load();
        Cards.Clear(); foreach (var c in d.Cards) Cards.Add(c);
        Panels.Clear(); foreach (var p in d.Panels) Panels.Add(p);
    }
}
