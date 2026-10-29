using System.Text.RegularExpressions;

namespace HotelKeeper.Models;

public static class Roles
{
    public const string Admin = "Admin", Receptionist = "Receptionist", Housekeeper = "Housekeeper", Guest = "Guest";
    public static readonly string[] Staff = { Admin, Receptionist, Housekeeper };
}

public static class RoomStatus
{
    public const string Available = "Available", Occupied = "Occupied", Dirty = "Dirty", Cleaning = "Cleaning", OutOfService = "OutOfService";
    public static readonly string[] All = { Available, Occupied, Dirty, Cleaning, OutOfService };
}

public static class ResStatus
{
    public const string Pending = "Pending", Confirmed = "Confirmed", CheckedIn = "CheckedIn", CheckedOut = "CheckedOut", Cancelled = "Cancelled";
    public static readonly string[] All = { Pending, Confirmed, CheckedIn, CheckedOut, Cancelled };
}

public static class Category
{
    public const string Maintenance = "Maintenance", Cleaning = "Cleaning", AdditionalItems = "AdditionalItems", GeneralAssistance = "GeneralAssistance";
    public static readonly string[] All = { Maintenance, Cleaning, AdditionalItems, GeneralAssistance };
}

public static class ReqStatus
{
    public const string Pending = "Pending", Acknowledged = "Acknowledged", InProgress = "InProgress", Fulfilled = "Fulfilled",
        Rejected = "Rejected", UnableToFulfill = "UnableToFulfill";
    public static readonly string[] Open = { Pending, Acknowledged, InProgress };
    public static bool IsOpen(string s) => Open.Contains(s);
}

public static class WorkStatus
{
    public const string Pending = "Pending", InProgress = "InProgress", Completed = "Completed", Cancelled = "Cancelled";
}

public static class ReportStatus
{
    public const string Open = "Open", Assigned = "Assigned", InProgress = "InProgress", Resolved = "Resolved", Closed = "Closed";
    public static readonly string[] Unresolved = { Open, Assigned, InProgress };
}

public static class Priorities
{
    public static readonly string[] All = { "Low", "Normal", "High", "Urgent" };
}

public static class Labels
{
    /// <summary>"OutOfService" → "Out of Service".</summary>
    public static string Of(string? s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
}

/// <summary>The signed-in user. All services receive one and enforce the role themselves.</summary>
public sealed class Session
{
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "";
    public int? GuestId { get; init; }
    public bool MustChangePassword { get; set; }

    public bool Is(params string[] roles) => roles.Contains(Role);

    public void Require(params string[] roles)
    {
        if (!roles.Contains(Role))
            throw new Data.AuthorizationException("You do not have permission to do this.");
    }

    public int RequireGuestId() => GuestId ?? throw new Data.AuthorizationException("Only guests can do this.");
}

public sealed record StatCard(string Label, string Value, string Hint = "", string Icon = "", string TargetPage = "");
public sealed record PanelRow(string Title, string Subtitle = "", string Badge = "");
public sealed record Panel(string Title, string Description, IReadOnlyList<PanelRow> Rows, string EmptyText = "Nothing to show.", string TargetPage = "");
public sealed record DashboardData(IReadOnlyList<StatCard> Cards, IReadOnlyList<Panel> Panels);
