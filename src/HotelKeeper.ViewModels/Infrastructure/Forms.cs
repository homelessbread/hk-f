using System.Globalization;

namespace HotelKeeper.ViewModels;

public enum FieldKind { Text, Multiline, Password, Integer, Decimal, Date, Combo, Check, Info }

public sealed record Choice(object? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One input in a form dialog / form page. The view binds to Text, Date, Checked and Selected depending on Kind.</summary>
public sealed class FormField : ObservableObject
{
    string text = "", error = "";
    DateTime? date;
    bool isChecked;
    Choice? selected;

    public required string Key { get; init; }
    public required string Label { get; init; }
    public FieldKind Kind { get; init; }
    public bool Required { get; init; }
    public string Hint { get; init; } = "";
    public IReadOnlyList<Choice> Choices { get; init; } = Array.Empty<Choice>();
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }

    public string Text { get => text; set { if (Set(ref text, value)) Error = ""; } }
    public DateTime? Date { get => date; set { if (Set(ref date, value)) Error = ""; } }
    public bool Checked { get => isChecked; set => Set(ref isChecked, value); }
    public Choice? Selected { get => selected; set { if (Set(ref selected, value)) Error = ""; } }
    public string Error { get => error; set { Set(ref error, value); Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    public bool IsText => Kind is FieldKind.Text or FieldKind.Integer or FieldKind.Decimal;
    public bool IsMultiline => Kind == FieldKind.Multiline;
    public bool IsPassword => Kind == FieldKind.Password;
    public bool IsDate => Kind == FieldKind.Date;
    public bool IsCombo => Kind == FieldKind.Combo;
    public bool IsCheck => Kind == FieldKind.Check;
    public bool IsInfo => Kind == FieldKind.Info;

    /// <summary>Validates and returns the typed value, or sets <see cref="Error"/> and returns false.</summary>
    public bool TryGet(out object? value)
    {
        value = null;
        switch (Kind)
        {
            case FieldKind.Info: return true;
            case FieldKind.Check: value = Checked; return true;
            case FieldKind.Combo:
                if (Required && Selected is null) return Fail("Choose an option.");
                value = Selected?.Value; return true;
            case FieldKind.Date:
                if (Required && Date is null) return Fail("Choose a date.");
                value = Date; return true;
            case FieldKind.Integer:
                if (Text.Trim().Length == 0) { if (Required) return Fail("Enter a whole number."); return true; }
                if (!int.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var i)) return Fail("Enter a whole number.");
                if (Min is { } mi && i < mi) return Fail($"Must be at least {mi}.");
                if (Max is { } ma && i > ma) return Fail($"Must be at most {ma}.");
                value = i; return true;
            case FieldKind.Decimal:
                if (Text.Trim().Length == 0) { if (Required) return Fail("Enter an amount."); return true; }
                if (!decimal.TryParse(Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d)) return Fail("Enter a number.");
                if (Min is { } m2 && d < m2) return Fail($"Must be at least {m2}.");
                if (Max is { } m3 && d > m3) return Fail($"Must be at most {m3}.");
                value = d; return true;
            default:
                if (Required && Text.Trim().Length == 0) return Fail("This field is required.");
                value = Kind == FieldKind.Password ? Text : Text.Trim(); return true;
        }
        bool Fail(string m) { Error = m; return false; }
    }

    // ---- factories -------------------------------------------------------------------------------------------
    public static FormField Txt(string key, string label, string? value = null, bool required = false, string hint = "") =>
        new() { Key = key, Label = label, Kind = FieldKind.Text, Required = required, Hint = hint, Text = value ?? "" };
    public static FormField Multi(string key, string label, string? value = null, bool required = false, string hint = "") =>
        new() { Key = key, Label = label, Kind = FieldKind.Multiline, Required = required, Hint = hint, Text = value ?? "" };
    public static FormField Pass(string key, string label, bool required = true, string hint = "") =>
        new() { Key = key, Label = label, Kind = FieldKind.Password, Required = required, Hint = hint };
    public static FormField Int(string key, string label, int? value = null, bool required = true, int? min = null, int? max = null) =>
        new() { Key = key, Label = label, Kind = FieldKind.Integer, Required = required, Min = min, Max = max, Text = value?.ToString() ?? "" };
    public static FormField Dec(string key, string label, decimal? value = null, bool required = true, decimal? min = null) =>
        new() { Key = key, Label = label, Kind = FieldKind.Decimal, Required = required, Min = min, Text = value?.ToString("0.##") ?? "" };
    public static FormField Day(string key, string label, DateTime? value = null, bool required = true) =>
        new() { Key = key, Label = label, Kind = FieldKind.Date, Required = required, Date = value };
    public static FormField Check(string key, string label, bool value = false) =>
        new() { Key = key, Label = label, Kind = FieldKind.Check, Checked = value };
    public static FormField InfoLine(string label, string value) =>
        new() { Key = "_info_" + label, Label = label, Kind = FieldKind.Info, Text = value };
    public static FormField Combo(string key, string label, IEnumerable<Choice> choices, object? selectedValue = null, bool required = true, string hint = "")
    {
        var list = choices.ToList();
        return new FormField { Key = key, Label = label, Kind = FieldKind.Combo, Required = required, Hint = hint, Choices = list,
            Selected = list.FirstOrDefault(c => Equals(c.Value, selectedValue)) };
    }
}

/// <summary>Result of a form dialog: typed values by field key.</summary>
public sealed class FormResult(Dictionary<string, object?> values)
{
    public T? Get<T>(string key) => values.TryGetValue(key, out var v) && v is T t ? t : default;
    public string Str(string key) => Get<string>(key) ?? "";
    public int Int(string key) => Get<int>(key);
    public decimal Dec(string key) => Get<decimal>(key);
    public bool Bool(string key) => Get<bool>(key);
    public DateTime Day(string key) => Get<DateTime>(key);
    public IReadOnlyDictionary<string, object?> Values => values;
}

/// <summary>View model of the form dialog; the WPF window binds to it and closes when Submit succeeds.</summary>
public sealed class FormDialogViewModel : ObservableObject
{
    public FormDialogViewModel(string title, string? message, IReadOnlyList<FormField> fields, string okText, bool danger = false)
    { Title = title; Message = message ?? ""; Fields = fields; OkText = okText; IsDanger = danger; }

    public string Title { get; }
    public string Message { get; }
    public string OkText { get; }
    public bool IsDanger { get; }
    public IReadOnlyList<FormField> Fields { get; }
    public FormResult? Result { get; private set; }

    /// <summary>Validates every field. Returns true (and sets <see cref="Result"/>) only when all are valid.</summary>
    public bool Submit()
    {
        var values = new Dictionary<string, object?>();
        var ok = true;
        foreach (var f in Fields)
        {
            if (f.TryGet(out var v)) values[f.Key] = v; else ok = false;
        }
        if (!ok) return false;
        Result = new FormResult(values);
        return true;
    }
}

public interface IDialogService
{
    /// <summary>Shows a modal form. Returns null when cancelled.</summary>
    Task<FormResult?> FormAsync(string title, string? message, IReadOnlyList<FormField> fields, string okText = "Save", bool danger = false);
    Task<bool> ConfirmAsync(string title, string message, string okText = "Confirm", bool danger = false);
    Task MessageAsync(string title, string message, bool isError = false);
    /// <summary>Read-only list of lines (history, one-time passwords, …).</summary>
    Task ShowLinesAsync(string title, string? message, IEnumerable<string> lines);
}
