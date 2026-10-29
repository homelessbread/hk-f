using HotelKeeper.Data;
using HotelKeeper.Models;
using HotelKeeper.Services;

namespace HotelKeeper.ViewModels;

/// <summary>Housekeeping work queue and the admin-only configuration pages.</summary>
public static class StaffPages
{
    // ================================================================================== housekeeping
    public static ListPageViewModel Housekeeping(PageContext ctx, string? title = null, string? description = null)
    {
        var s = ctx.Session; var svc = new HousekeepingService(s); var hk = s.Role == Roles.Housekeeper; var worker = s.Role is Roles.Housekeeper or Roles.Admin;
        var p = new ListPageViewModel(ctx, title ?? (hk ? "My cleaning tasks" : "Housekeeping"), description ?? (hk ? "Your assigned rooms and the pool of unassigned ones." : "Cleaning tasks, who is on them and how far along they are."),
            new[] { new ColumnDef("Room", nameof(TaskRow.RoomNumber), Width: 70), new ColumnDef("Room status", nameof(TaskRow.RoomStatus), ColumnKind.Badge, 125), new ColumnDef("Task", nameof(TaskRow.TaskType), ColumnKind.Badge, 110),
                    new ColumnDef("Priority", nameof(TaskRow.Priority), ColumnKind.Badge, 95), new ColumnDef("Status", nameof(TaskRow.Status), ColumnKind.Badge, 110), new ColumnDef("Assigned to", nameof(TaskRow.AssignedTo), Width: 140),
                    new ColumnDef("Checklist", nameof(TaskRow.Progress), Width: 80), new ColumnDef("Open issues", nameof(TaskRow.OpenIssues), ColumnKind.Number, 90), new ColumnDef("Notes", nameof(TaskRow.Notes)),
                    new ColumnDef("Created", nameof(TaskRow.CreatedAt), ColumnKind.DateTime, 140) },
            async q => (await svc.ListAsync(q.Str("scope") ?? "open", q.SearchOrNull)).Cast<object>().ToList(), "No cleaning tasks right now.", searchHint: "Search room number…");
        p.AddTabs("scope", ("Open", "open"), ("Completed", "done"), ("All", "all"));

        if (worker)
        {
            p.Act<TaskRow>("Start cleaning", "play", async t =>
            {
                await svc.StartAsync(t.Id); p.StatusMessage = $"Room {t.RoomNumber} is now being cleaned."; await p.ReloadAsync();
            }, t => t.Status == WorkStatus.Pending, primary: true);
            p.Act<TaskRow>("Checklist", "list-checks", async t =>
            {
                var items = await svc.ChecklistAsync(t.Id);
                if (items.Count == 0) { await ctx.Dialogs.MessageAsync("No checklist", "This task has no checklist items.", false); return; }
                var f = await ctx.Dialogs.FormAsync($"Room {t.RoomNumber}: cleaning checklist", "Tick what is done. You can save and come back later.", items.Select(i => FormField.Check("c" + i.Id, i.Label, i.IsDone)).ToList(), "Save checklist");
                if (f is null) return;
                await svc.SetChecklistAsync(t.Id, items.ToDictionary(i => i.Id, i => f.Bool("c" + i.Id)));
                p.StatusMessage = "Checklist saved."; await p.ReloadAsync();
            }, t => t.Status == WorkStatus.InProgress);
            p.Act<TaskRow>("Complete", "check", async t =>
            {
                var f = await ctx.Dialogs.FormAsync($"Complete cleaning: room {t.RoomNumber}", "Every checklist item must be done. The room becomes Available only if nothing blocks it.", new[] { FormField.Multi("notes", "Notes (optional)") }, "Complete cleaning");
                if (f is null) return;
                var outcome = await svc.CompleteAsync(t.Id, f.Str("notes"));
                p.StatusMessage = outcome; await p.ReloadAsync();
            }, t => t.Status == WorkStatus.InProgress);
            p.Act<TaskRow>("Report problem", "alert", t => SharedPages.ReportProblem(ctx, p, t.RoomId));
        }
        else
        {
            p.Act("New task", "plus", async () =>
            {
                var rooms = (await new RoomService(s).ListAsync()).Where(r => r.Status != RoomStatus.OutOfService).Select(r => new Choice(r.Id, r.Display + $"  [{Labels.Of(r.Status)}]")).ToList();
                var f = await ctx.Dialogs.FormAsync("New housekeeping task", "For an extra cleaning or inspection. Check-out creates its own task automatically.", new[]
                {
                    FormField.Combo("room", "Room", rooms), FormField.Combo("pri", "Priority", SharedPages.Strs(Priorities.All), "Normal"), FormField.Multi("notes", "What needs doing?", required: true),
                }, "Create task");
                if (f is null) return;
                await svc.CreateTaskAsync(f.Get<int>("room"), f.Get<string>("pri")!, f.Str("notes")); p.StatusMessage = "Task created and housekeeping notified."; await p.ReloadAsync();
            }, primary: true);
            p.Act<TaskRow>("Assign", "user", async t =>
            {
                var staff = await new StaffService(s).AssigneesAsync(Roles.Housekeeper);
                var f = await ctx.Dialogs.FormAsync($"Room {t.RoomNumber}: assign task", null, new[] { FormField.Combo("who", "Housekeeper", staff.Select(x => new Choice(x.Id, x.FullName)), t.AssignedToId, required: false, hint: "Leave empty to return it to the pool.") }, "Assign");
                if (f is null) return;
                await svc.AssignAsync(t.Id, f.Get<int?>("who")); p.StatusMessage = "Task assignment saved."; await p.ReloadAsync();
            }, t => t.Status is WorkStatus.Pending or WorkStatus.InProgress);
            p.Act<TaskRow>("Cancel task", "x", async t =>
            {
                if (!await ctx.Dialogs.ConfirmAsync("Cancel task", $"Cancel the {t.TaskType.ToLower()} task for room {t.RoomNumber}?", "Cancel task", danger: true)) return;
                await svc.CancelAsync(t.Id); p.StatusMessage = "Task cancelled."; await p.ReloadAsync();
            }, t => t.Status is WorkStatus.Pending or WorkStatus.InProgress, danger: true);
        }
        return p;
    }

    // ================================================================================== admin: staff
    public static ListPageViewModel Staff(PageContext ctx)
    {
        var svc = new StaffService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Staff & accounts", "Create staff accounts, assign roles and control access.",
            new[] { new ColumnDef("Name", nameof(StaffRow.FullName), Width: 190), new ColumnDef("Username", nameof(StaffRow.Username), Width: 130), new ColumnDef("Role", nameof(StaffRow.Role), ColumnKind.Badge, 120),
                    new ColumnDef("Email", nameof(StaffRow.Email), Width: 200), new ColumnDef("Phone", nameof(StaffRow.Phone), Width: 120), new ColumnDef("Active", nameof(StaffRow.IsActive), ColumnKind.Bool, 70),
                    new ColumnDef("Last sign-in", nameof(StaffRow.LastLoginAt), ColumnKind.DateTime, 150) },
            async q => (await svc.ListAsync()).Where(x => q.SearchOrNull is not { } t || x.FullName.Contains(t, StringComparison.OrdinalIgnoreCase) || x.Username.Contains(t, StringComparison.OrdinalIgnoreCase)).Cast<object>().ToList(),
            "No staff accounts.", searchHint: "Search name or username…");
        p.Act("Add staff", "user-plus", async () =>
        {
            var f = await ctx.Dialogs.FormAsync("Add staff account", "A temporary password is generated. The person must change it at first sign-in.", Fields(null), "Create account");
            if (f is null) return;
            var pw = await svc.CreateAsync(f.Str("user"), f.Str("name"), f.Str("email"), f.Str("phone"), f.Get<string>("role")!);
            await ctx.Dialogs.ShowLinesAsync("Account created", "Give these sign-in details to the staff member. The password is shown only now.", new[] { $"Username:  {f.Str("user")}", $"Temporary password:  {pw}" });
            p.StatusMessage = $"{f.Str("name")} was added."; await p.ReloadAsync();
        }, primary: true);
        p.Act<StaffRow>("Edit", "pencil", async u =>
        {
            var f = await ctx.Dialogs.FormAsync($"Edit {u.FullName}", null, Fields(u), "Save changes");
            if (f is null) return;
            await svc.UpdateAsync(u.Id, f.Str("name"), f.Str("email"), f.Str("phone"), f.Get<string>("role")!); p.StatusMessage = "Account updated."; await p.ReloadAsync();
        });
        p.Act<StaffRow>("Enable / disable", "power", async u =>
        {
            var disabling = u.IsActive;
            if (!await ctx.Dialogs.ConfirmAsync(disabling ? "Disable account" : "Enable account", disabling ? $"{u.FullName} will no longer be able to sign in." : $"Allow {u.FullName} to sign in again?", disabling ? "Disable" : "Enable", danger: disabling)) return;
            await svc.SetActiveAsync(u.Id, !u.IsActive); p.StatusMessage = disabling ? "Account disabled." : "Account enabled."; await p.ReloadAsync();
        });
        p.Act<StaffRow>("Reset password", "key", async u =>
        {
            if (!await ctx.Dialogs.ConfirmAsync("Reset password", $"Create a new temporary password for {u.FullName}?", "Reset")) return;
            var pw = await svc.ResetPasswordAsync(u.Id);
            await ctx.Dialogs.ShowLinesAsync("Password reset", "Give this to the staff member. It is shown only now.", new[] { $"Username:  {u.Username}", $"Temporary password:  {pw}" });
        });
        return p;

        static IReadOnlyList<FormField> Fields(StaffRow? u) => new[]
        {
            u is null ? FormField.Txt("user", "Username", required: true, hint: "3+ characters, no spaces.") : FormField.InfoLine("Username", u.Username),
            FormField.Txt("name", "Full name", u?.FullName, true), FormField.Txt("email", "Email", u?.Email), FormField.Txt("phone", "Phone", u?.Phone),
            FormField.Combo("role", "Role", Roles.Staff.Select(r => new Choice(r, r)), u?.Role ?? Roles.Housekeeper),
        };
    }

    // ================================================================================== admin: catalogue, services, settings, logs
    public static ListPageViewModel ItemCatalog(PageContext ctx)
    {
        var svc = new RequestService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Requestable items", "What guests can ask for, how many, and the fulfilment rules.",
            new[] { new ColumnDef("Item", nameof(RequestableItemRow.Name), Width: 180), new ColumnDef("Category", nameof(RequestableItemRow.ItemCategory), Width: 130), new ColumnDef("Max per request", nameof(RequestableItemRow.MaxQuantity), ColumnKind.Number, 120),
                    new ColumnDef("Fulfilment rule", nameof(RequestableItemRow.Rule)), new ColumnDef("Available", nameof(RequestableItemRow.IsActive), ColumnKind.Bool, 90) },
            async _ => (await svc.ItemsAsync(false)).Cast<object>().ToList(), "No items yet.", searchable: false);
        p.Act("Add item", "plus", () => Edit(null), primary: true);
        p.Act<RequestableItemRow>("Edit", "pencil", i => Edit(i));
        return p;
        async Task Edit(RequestableItemRow? i)
        {
            var f = await ctx.Dialogs.FormAsync(i is null ? "Add requestable item" : $"Edit {i.Name}", null, new[]
            {
                FormField.Txt("name", "Item", i?.Name, true), FormField.Txt("cat", "Category", i?.ItemCategory ?? "Supplies", true), FormField.Int("max", "Maximum per request", i?.MaxQuantity ?? 4, true, 1, 99),
                FormField.Multi("rule", "Fulfilment rule", i?.Rule), FormField.Check("active", "Available to guests", i?.IsActive ?? true),
            }, "Save");
            if (f is null) return;
            await svc.SaveItemAsync(i?.Id, f.Str("name"), f.Str("cat"), f.Int("max"), f.Str("rule"), f.Bool("active")); p.StatusMessage = "Item saved."; await p.ReloadAsync();
        }
    }

    public static ListPageViewModel ChecklistTemplate(PageContext ctx)
    {
        var svc = new RequestService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Cleaning checklist", "The steps copied onto every new cleaning task. Changes apply to future tasks.",
            new[] { new ColumnDef("Order", nameof(ChecklistTemplateRow.SortOrder), ColumnKind.Number, 80), new ColumnDef("Step", nameof(ChecklistTemplateRow.Label)), new ColumnDef("Active", nameof(ChecklistTemplateRow.IsActive), ColumnKind.Bool, 80) },
            async _ => (await svc.ChecklistTemplateAsync()).Cast<object>().ToList(), "No checklist steps.", searchable: false);
        p.Act("Add step", "plus", () => Edit(null), primary: true);
        p.Act<ChecklistTemplateRow>("Edit", "pencil", c => Edit(c));
        return p;
        async Task Edit(ChecklistTemplateRow? c)
        {
            var f = await ctx.Dialogs.FormAsync(c is null ? "Add checklist step" : "Edit checklist step", null, new[]
            { FormField.Txt("label", "Step", c?.Label, true), FormField.Int("order", "Order", c?.SortOrder ?? 99, true, 0, 999), FormField.Check("active", "Active", c?.IsActive ?? true) }, "Save");
            if (f is null) return;
            await svc.SaveChecklistItemAsync(c?.Id, f.Str("label"), f.Int("order"), f.Bool("active")); p.StatusMessage = "Checklist updated."; await p.ReloadAsync();
        }
    }

    public static ListPageViewModel Settings(PageContext ctx)
    {
        var svc = new SettingsService(ctx.Session);
        var p = new ListPageViewModel(ctx, "Settings", "Hotel-wide settings. Every change is written to the audit log.",
            new[] { new ColumnDef("Setting", nameof(SettingRow.SettingKey), Width: 220), new ColumnDef("Value", nameof(SettingRow.SettingValue), Width: 220), new ColumnDef("What it does", nameof(SettingRow.Description)) },
            async _ => (await svc.ListAsync()).Cast<object>().ToList(), "No settings.", searchable: false);
        p.Act<SettingRow>("Change value", "pencil", async r =>
        {
            var f = await ctx.Dialogs.FormAsync($"Change {r.SettingKey}", r.Description, new[] { FormField.Txt("v", "Value", r.SettingValue, true) }, "Save");
            if (f is null) return;
            await svc.SetAsync(r.SettingKey, f.Str("v")); p.StatusMessage = "Setting saved."; await p.ReloadAsync();
        }, primary: true);
        return p;
    }

    public static ListPageViewModel AuditLog(PageContext ctx)
    {
        var svc = new AuditService(ctx.Session);
        return new ListPageViewModel(ctx, "Activity log", "Who did what and when: sign-ins, room changes, check-ins, check-outs and account changes.",
            new[] { new ColumnDef("When", nameof(AuditRow.CreatedAt), ColumnKind.DateTime, 150), new ColumnDef("User", nameof(AuditRow.User), Width: 140), new ColumnDef("Action", nameof(AuditRow.Action), Width: 180),
                    new ColumnDef("Record", nameof(AuditRow.EntityType), Width: 100), new ColumnDef("ID", nameof(AuditRow.EntityId), ColumnKind.Number, 60), new ColumnDef("Details", nameof(AuditRow.Details)) },
            async q => (await svc.ListAsync(q.SearchOrNull)).Cast<object>().ToList(), "No activity recorded.", searchHint: "Search user, action or details…");
    }
}
