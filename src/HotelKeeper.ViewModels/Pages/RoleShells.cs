using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>
/// Builds the sidebar, rail and pages for each role. A role gets only the pages listed here, and the services behind
/// them check the role again, so there is no way to reach another role's screen or data by changing a UI value.
/// </summary>
public static class RoleShells
{
    public static ShellViewModel Create(PageContext ctx, string hotelName) => ctx.Session.Role switch
    {
        Roles.Admin => Admin(ctx, hotelName),
        Roles.Receptionist => Receptionist(ctx, hotelName),
        Roles.Housekeeper => Housekeeper(ctx, hotelName),
        Roles.Guest => Guest(ctx, hotelName),
        _ => throw new Data.AuthorizationException("This account has no interface.")
    };

    static (string key, string title, string icon, Func<PageViewModel> create) Nav(string key, string title, string icon, Func<PageViewModel> create) => (key, title, icon, create);

    static ShellViewModel Admin(PageContext ctx, string name)
    {
        var d = new DashboardService(ctx.Session);
        return new ShellViewModel(ctx, name, new[]
        {
            new NavDefinition("Overview", "home", new[]
            {
                Nav("dashboard", "Dashboard", "layout", () => new DashboardPageViewModel(ctx, "Dashboard", "How the hotel is doing right now.", d.AdminAsync,
                    new[] { new QuickAction("Guest requests", "inbox", "requests"), new QuickAction("Maintenance", "wrench", "maintenance") })),
            }),
            new NavDefinition("Hotel", "bed", new[]
            {
                Nav("rooms", "Rooms", "bed", () => SharedPages.Rooms(ctx, SharedPages.RoomMode.Manage)),
                Nav("types", "Room types", "tag", () => SharedPages.RoomTypes(ctx)),
                Nav("items", "Room items", "package", () => SharedPages.RoomItems(ctx)),
                Nav("guests", "Guests", "users", () => SharedPages.Guests(ctx, "Guests", "Guest accounts and records. Changes are written to the audit log.")),
                Nav("staff", "Staff & accounts", "shield", () => StaffPages.Staff(ctx)),
            }),
            new NavDefinition("Operations", "clipboard", new[]
            {
                Nav("status", "Room status", "activity", () => SharedPages.Rooms(ctx, SharedPages.RoomMode.Status)),
                Nav("housekeeping", "Housekeeping", "sparkles", () => StaffPages.Housekeeping(ctx)),
                Nav("requests", "Guest requests", "inbox", () => SharedPages.Requests(ctx)),
                Nav("maintenance", "Maintenance reports", "wrench", () => SharedPages.Maintenance(ctx)),
                Nav("stays", "Stays", "history", () => SharedPages.StayHistory(ctx, "Stays", "Every stay, from check-in to departure.")),
            }),
            new NavDefinition("System", "settings", new[]
            {
                Nav("catalog", "Requestable items", "list", () => StaffPages.ItemCatalog(ctx)),
                Nav("checklist", "Cleaning checklist", "list-checks", () => StaffPages.ChecklistTemplate(ctx)),
                Nav("audit", "Activity log", "scroll", () => StaffPages.AuditLog(ctx)),
                Nav("settings", "Settings", "settings", () => StaffPages.Settings(ctx)),
            }),
            new NavDefinition("Archives", "archive", new[]
            {
                Nav("arch-rooms", "Room archive", "archive", () => SharedPages.Rooms(ctx, SharedPages.RoomMode.Manage, archivedOnly: true, title: "Room archive", description: "Archived rooms and their history. Nothing is ever deleted.")),
                Nav("arch-requests", "Request archive", "archive", () => SharedPages.Requests(ctx, archiveView: true, title: "Request archive", description: "Archived guest requests.")),
                Nav("arch-reports", "Report archive", "archive", () => SharedPages.Maintenance(ctx, archiveView: true, title: "Report archive", description: "Archived maintenance reports.")),
            }),
        });
    }

    static ShellViewModel Receptionist(PageContext ctx, string name)
    {
        var d = new DashboardService(ctx.Session);
        return new ShellViewModel(ctx, name, new[]
        {
            new NavDefinition("Front Desk", "concierge", new[]
            {
                Nav("dashboard", "Dashboard", "layout", () => new DashboardPageViewModel(ctx, "Front Desk", "Today's arrivals, departures and rooms.", d.ReceptionistAsync,
                    new[] { new QuickAction("Guest directory", "users", "guests"), new QuickAction("Room status", "bed", "rooms") })),
                Nav("guests", "Guest", "users", () => SharedPages.Guests(ctx, "Guest", "Guest accounts and records.")),
                Nav("rooms", "Rooms", "bed", () => SharedPages.Rooms(ctx, SharedPages.RoomMode.Status, title: "Rooms", description: "Room status and guest occupancy.")),
            }),
            new NavDefinition("Rooms & Services", "bed", new[]
            {
                Nav("housekeeping", "Housekeeping", "sparkles", () => StaffPages.Housekeeping(ctx)),
                Nav("requests", "Guest Request", "inbox", () => SharedPages.Requests(ctx, title: "Guest Request")),
                Nav("maintenance", "Maintenance Reports", "wrench", () => SharedPages.Maintenance(ctx, title: "Maintenance Reports")),
            }),
            new NavDefinition("Stays", "history", new[]
            {
                Nav("stays", "Stays", "history", () => SharedPages.StayHistory(ctx, "Stays", "Current and past stays, check-ins and check-outs.")),
            }),
        });
    }

    static ShellViewModel Housekeeper(PageContext ctx, string name)
    {
        var d = new DashboardService(ctx.Session);
        return new ShellViewModel(ctx, name, new[]
        {
            new NavDefinition("My work", "sparkles", new[]
            {
                Nav("dashboard", "Dashboard", "layout", () => new DashboardPageViewModel(ctx, "My day", "Rooms to clean and requests waiting for you.", d.HousekeeperAsync,
                    new[] { new QuickAction("My cleaning tasks", "sparkles", "tasks"), new QuickAction("Item requests", "package", "requests") })),
                Nav("tasks", "Cleaning tasks", "sparkles", () => StaffPages.Housekeeping(ctx)),
                Nav("requests", "Cleaning & item requests", "package", () => SharedPages.Requests(ctx, title: "Cleaning & item requests", description: "Requests from guests for cleaning and supplies.", housekeepingOnly: true)),
                Nav("problems", "Problem reports", "wrench", () => SharedPages.Maintenance(ctx, title: "Problem reports", description: "Report damage or unsafe rooms, and update the reports assigned to you.")),
            }),
            new NavDefinition("Rooms", "bed", new[]
            {
                Nav("rooms", "Room status", "bed", () => SharedPages.Rooms(ctx, SharedPages.RoomMode.View, title: "Room status", description: "Which rooms are dirty, being cleaned or ready.")),
            }),
        });
    }

    static ShellViewModel Guest(PageContext ctx, string name)
    {
        var d = new DashboardService(ctx.Session);
        return new ShellViewModel(ctx, name, new[]
        {
            new NavDefinition("My stay", "bed", new[]
            {
                Nav("dashboard", "Dashboard", "layout", () => new DashboardPageViewModel(ctx, $"Welcome, {ctx.Session.FullName.Split(' ')[0]}", "Your stay and bookings at a glance.", d.GuestAsync,
                    new[] { new QuickAction("Find a room", "search", "find"), new QuickAction("My requests", "inbox", "requests"), new QuickAction("My reservations", "calendar", "reservations") })),
                Nav("find", "Find a room", "search", () => GuestPages.FindRoom(ctx)),
                Nav("reservations", "My reservations", "calendar", () => GuestPages.MyReservations(ctx)),
                Nav("requests", "My requests", "inbox", () => GuestPages.MyRequests(ctx)),
            }),
            new NavDefinition("Services", "concierge", new[]
            {
                Nav("room-info", "Room information", "key", () => GuestPages.RoomInformation(ctx)),
            }),
        });
    }
}
